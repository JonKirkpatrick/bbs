using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel that owns the bot deployment workflow, including runtime launch,
/// agent control socket handshake, and deployment session persistence.
/// </summary>
public sealed class DeploymentServiceViewModel : ViewModelBase
{
    private const int BotTcpDefaultPort = 8080;
    private const int DeployHandshakeTimeoutMs = 3000;
    private const int DeployControlSocketReadyTimeoutMs = 8000;
    private const string ServerAccessServerIdMetadataKey = "server_access.server_id";
    private const string ServerAccessSessionIdMetadataKey = "server_access.session_id";
    private const string ServerAccessOwnerTokenMetadataKey = "server_access.owner_token";
    private const string ServerAccessDashboardEndpointMetadataKey = "server_access.dashboard_endpoint";

    private readonly IClientStorage _storage;
    private readonly IBotOrchestrationService _orchestration;
    private readonly IClientLogger _logger;
    private readonly SessionServiceViewModel _sessionService;

    private string _lastDeploymentMessage = "Select a bot and server to deploy.";

    public DeploymentServiceViewModel(
        IClientStorage storage,
        IBotOrchestrationService orchestration,
        IClientLogger logger,
        SessionServiceViewModel sessionService)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
    }

    public string LastDeploymentMessage
    {
        get => _lastDeploymentMessage;
        private set
        {
            if (_lastDeploymentMessage == value)
            {
                return;
            }

            _lastDeploymentMessage = value;
            OnPropertyChanged();
        }
    }

    public void DeploySelectedBotToSelectedServer(BotSummaryItem bot, ServerSummaryItem server)
    {
        var sourceProfile = bot.ToProfile();
        var runtimeProfile = BuildRuntimeInstanceProfile(sourceProfile);
        var controlSocketPath = DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeProfile.BotId);

        void EnsureRuntimeReady()
        {
            var launchResult = _orchestration.LaunchBotAsync(runtimeProfile).GetAwaiter().GetResult();
            if (!launchResult.Succeeded || !launchResult.RuntimeState.IsAttached)
            {
                throw new InvalidOperationException($"Deploy failed while starting bot: {launchResult.Message}");
            }

            DeploymentTransportHelpers.WaitForControlSocketReady(controlSocketPath, DeployControlSocketReadyTimeoutMs);
        }

        EnsureRuntimeReady();

        RegisterHandshakeResult? registerResponse = null;
        var registered = false;
        Exception? lastRegisterFailure = null;
        for (var attempt = 1; attempt <= 3 && !registered; attempt++)
        {
            try
            {
                registerResponse = RegisterBotSessionViaAgentControl(server, runtimeProfile);
                registered = true;
            }
            catch (Exception ex) when (DeploymentTransportHelpers.TryResolveSocketErrorCode(ex, out _))
            {
                lastRegisterFailure = ex;
                EnsureRuntimeReady();
                Thread.Sleep(150 * attempt);
            }
        }

        if (!registered)
        {
            throw new InvalidOperationException("Deploy failed: unable to complete control handshake after retries.", lastRegisterFailure);
        }

        if (registerResponse is null)
        {
            throw new InvalidOperationException("Deploy failed: handshake retries completed without response payload.");
        }

        var runtimeMetadata = new Dictionary<string, string>(runtimeProfile.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            [ServerAccessServerIdMetadataKey] = server.ServerId,
            [ServerAccessSessionIdMetadataKey] = registerResponse.SessionId
        };

        if (!string.IsNullOrWhiteSpace(registerResponse.OwnerToken))
        {
            runtimeMetadata[ServerAccessOwnerTokenMetadataKey] = registerResponse.OwnerToken;
        }

        if (!string.IsNullOrWhiteSpace(registerResponse.DashboardEndpoint))
        {
            runtimeMetadata[ServerAccessDashboardEndpointMetadataKey] = registerResponse.DashboardEndpoint;
        }

        var runtimeAttachedProfile = BotProfile.Create(
            botId: runtimeProfile.BotId,
            name: runtimeProfile.Name,
            launchPath: runtimeProfile.LaunchPath,
            avatarImagePath: runtimeProfile.AvatarImagePath,
            launchArgs: runtimeProfile.LaunchArgs,
            metadata: runtimeMetadata,
            createdAtUtc: runtimeProfile.CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);

        var runtimeSessionState = new AgentRuntimeState(
            BotId: runtimeProfile.BotId,
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsAttached: true,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var activeSessionState = new AgentRuntimeState(
            BotId: sourceProfile.BotId,
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsAttached: true,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        _storage.UpsertBotProfileAsync(runtimeAttachedProfile).GetAwaiter().GetResult();
        _storage.UpsertAgentRuntimeStateAsync(runtimeSessionState).GetAwaiter().GetResult();
        _storage.UpsertAgentRuntimeStateAsync(activeSessionState).GetAwaiter().GetResult();

        _sessionService.AddActiveDeployConnection(sourceProfile.BotId, registerResponse.SessionId);
        _sessionService.SetActiveServerAccess(
            sourceProfile.BotId,
            registerResponse.SessionId,
            runtimeProfile.BotId,
            runtimeProfile.Name,
            server.ServerId,
            registerResponse.OwnerToken,
            registerResponse.DashboardEndpoint);

        LastDeploymentMessage = $"Deployed {sourceProfile.Name} to {server.Name}; active session established.";

        _logger.Log(LogLevel.Information, "bot_deploy_attached", "Bot deploy completed server register handshake and attached active session metadata.",
            new Dictionary<string, string>
            {
                ["bot_id"] = sourceProfile.BotId,
                ["runtime_bot_id"] = runtimeProfile.BotId,
                ["runtime_bot_name"] = runtimeProfile.Name,
                ["server_id"] = server.ServerId,
                ["session_id"] = registerResponse.SessionId,
                ["dashboard_endpoint"] = registerResponse.DashboardEndpoint
            });
    }

    private RegisterHandshakeResult RegisterBotSessionViaAgentControl(ServerSummaryItem server, BotProfile profile)
    {
        var controlSocketPath = DeploymentTransportHelpers.BuildAgentControlSocketPath(profile.BotId);
        var agentTargets = BuildAgentServerTargetEndpointCandidates(server);
        AgentControlResponse? connectReply = null;
        string? lastConnectError = null;

        foreach (var agentTarget in agentTargets)
        {
            var candidateReply = DeploymentTransportHelpers.SendAgentControlRequest(
                controlSocketPath,
                "server_connect",
                new Dictionary<string, object>
                {
                    ["server"] = agentTarget
                },
                DeployHandshakeTimeoutMs);

            if (string.Equals(candidateReply.Type, "server_connect", StringComparison.OrdinalIgnoreCase))
            {
                connectReply = candidateReply;
                break;
            }

            if (string.Equals(candidateReply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                lastConnectError = candidateReply.Message;
                continue;
            }

            lastConnectError = $"Unexpected control reply type {candidateReply.Type} from agent.";
        }

        if (connectReply is null)
        {
            throw new InvalidOperationException($"Failed server_connect via agent. {lastConnectError ?? "No response"}");
        }

        var accessReply = DeploymentTransportHelpers.SendAgentControlRequest(controlSocketPath, "server_access", new Dictionary<string, object>(), DeployHandshakeTimeoutMs);
        if (string.Equals(accessReply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(accessReply.Message);
        }

        if (!string.Equals(accessReply.Type, "server_access", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected server_access reply type {accessReply.Type}.");
        }

        var sessionId = accessReply.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("Agent did not return a valid session_id after server_connect.");
        }

        return new RegisterHandshakeResult(
            SessionId: sessionId,
            OwnerToken: accessReply.OwnerToken,
            DashboardEndpoint: ServerServiceViewModel.NormalizeDashboardEndpoint(accessReply.DashboardEndpoint, accessReply.DashboardHost, accessReply.DashboardPort, server.UseTls));
    }

    private static IReadOnlyList<string> BuildAgentServerTargetEndpointCandidates(ServerSummaryItem server)
    {
        var botPort = BotTcpDefaultPort;
        if (server.Metadata.TryGetValue("bot_port", out var rawBotPort) && int.TryParse(rawBotPort, out var parsedPort) && parsedPort is > 0 and <= 65535)
        {
            botPort = parsedPort;
        }

        var candidates = new List<string>();
        var hostCandidates = DeploymentTransportHelpers.BuildHostCandidates(server.Host).ToList();

        if (hostCandidates.Count == 0)
        {
            hostCandidates.Add(server.Host.Trim());
        }

        foreach (var host in hostCandidates)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                candidates.Add($"{host}:{botPort}");
            }
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BotProfile BuildRuntimeInstanceProfile(BotProfile sourceProfile)
    {
        var runtimeSuffix = Guid.NewGuid().ToString("N")[..6];
        var runtimeName = $"{sourceProfile.Name}-{runtimeSuffix}";
        var runtimeBotId = $"{sourceProfile.BotId}-{runtimeSuffix}";
        var runtimeMetadata = new Dictionary<string, string>(sourceProfile.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["source_bot_id"] = sourceProfile.BotId,
            ["source_bot_name"] = sourceProfile.Name,
            ["runtime_instance_suffix"] = runtimeSuffix,
            ["runtime_instance"] = "true"
        };

        return BotProfile.Create(
            botId: runtimeBotId,
            name: runtimeName,
            launchPath: sourceProfile.LaunchPath,
            avatarImagePath: sourceProfile.AvatarImagePath,
            launchArgs: sourceProfile.LaunchArgs,
            metadata: runtimeMetadata,
            createdAtUtc: DateTimeOffset.UtcNow,
            updatedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed record RegisterHandshakeResult(string SessionId, string OwnerToken, string DashboardEndpoint);
}