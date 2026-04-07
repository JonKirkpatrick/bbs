using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void DeploySelectedBotToSelectedServer()
    {
        if (!CanDeploySelectedBot())
        {
            _botService.BotEditorMessage = "Deploy is only available when a live server is selected.";
            return;
        }

        var bot = SelectedBot;
        var server = SelectedServer;
        if (bot is null)
        {
            _botService.BotEditorMessage = "Select a bot before deploy.";
            return;
        }

        if (server is null)
        {
            _botService.BotEditorMessage = "Deploy requires a selected server.";
            return;
        }

        try
        {
            var sourceProfile = bot.ToProfile();
            var runtimeProfile = BuildRuntimeInstanceProfile(sourceProfile);
            var controlSocketPath = BuildAgentControlSocketPath(runtimeProfile.BotId);

            void EnsureRuntimeReady()
            {
                var launchResult = _orchestration.LaunchBotAsync(runtimeProfile).GetAwaiter().GetResult();
                if (!launchResult.Succeeded || !launchResult.RuntimeState.IsAttached)
                {
                    throw new InvalidOperationException($"Deploy failed while starting bot: {launchResult.Message}");
                }

                WaitForControlSocketReady(controlSocketPath, DeployControlSocketReadyTimeoutMs);

                LoadBotsFromStorage();
                SelectedBot = FindBotById(sourceProfile.BotId);
            }

            // Deploy always creates a fresh runtime instance for multi-session support.
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
                catch (Exception ex) when (TryResolveSocketErrorCode(ex, out _))
                {
                    lastRegisterFailure = ex;
                    // Relaunch for subsequent attempts to recover from stale or short-lived runtime/socket state.
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

            SetActiveServerAccess(
                sourceProfile.BotId,
                registerResponse.SessionId,
                runtimeProfile.BotId,
                runtimeProfile.Name,
                server.ServerId,
                registerResponse.OwnerToken,
                registerResponse.DashboardEndpoint);

            LoadBotsFromStorage();
            SelectedBot = FindBotById(sourceProfile.BotId);
            RefreshActiveBotSessionsProjection();
            TriggerServerAccessRefresh();
            _botService.BotEditorMessage = $"Deployed {sourceProfile.Name} to {server.Name}; active session established.";

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
        catch (SocketException socketException)
        {
            HandleOrchestrationException("deploy", bot.BotId, $"socket_{socketException.SocketErrorCode}".ToLowerInvariant(), socketException);
        }
        catch (InvalidOperationException invalidOperationException)
        {
            HandleOrchestrationException("deploy", bot.BotId, "deploy_runtime_unavailable", invalidOperationException);
        }
        catch (Exception ex)
        {
            if (TryResolveSocketErrorCode(ex, out var socketErrorCode))
            {
                HandleOrchestrationException("deploy", bot.BotId, socketErrorCode, ex);
                return;
            }

            HandleOrchestrationException("deploy", bot.BotId, "deploy_attach_failed", ex);
        }
    }

    private RegisterHandshakeResult RegisterBotSessionViaAgentControl(ServerSummaryItem server, BotProfile profile)
    {
        var controlSocketPath = BuildAgentControlSocketPath(profile.BotId);
        var agentTargets = BuildAgentServerTargetEndpointCandidates(server);
        AgentControlResponse? connectReply = null;
        string? lastConnectError = null;

        foreach (var agentTarget in agentTargets)
        {
            var candidateReply = SendAgentControlRequest(
                controlSocketPath,
                "server_connect",
                new Dictionary<string, object>
                {
                    ["server"] = agentTarget
                });

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

        var accessReply = SendAgentControlRequest(controlSocketPath, "server_access", new Dictionary<string, object>());
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
        var hostCandidates = BuildHostCandidates(server.Host);

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

    private static List<string> BuildHostCandidates(string rawHost)
    {
        var candidates = new List<string>();
        var trimmed = rawHost.Trim();
        if (trimmed.Length == 0)
        {
            return candidates;
        }

        candidates.Add(trimmed);

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            candidates.Add(absolute.Host);
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate($"tcp://{trimmed}", UriKind.Absolute, out var implicitUri) &&
            !string.IsNullOrWhiteSpace(implicitUri.Host))
        {
            candidates.Add(implicitUri.Host);
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            candidates.Add(trimmed[1..^1]);
        }

        if (IPAddress.TryParse(trimmed, out var parsedIp))
        {
            candidates.Add(parsedIp.ToString());
        }

        if (TryNormalizeThreePartLoopback(trimmed, out var normalizedLoopback))
        {
            candidates.Add(normalizedLoopback);
        }

        if (IsLikelyLoopback(trimmed))
        {
            candidates.Add("127.0.0.1");
            candidates.Add("localhost");
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryNormalizeThreePartLoopback(string value, out string normalized)
    {
        normalized = string.Empty;
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], "127", StringComparison.Ordinal) || !string.Equals(parts[1], "0", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[2], out var finalOctet) || finalOctet is < 0 or > 255)
        {
            return false;
        }

        normalized = $"127.0.0.{finalOctet}";
        return true;
    }

    private static bool IsLikelyLoopback(string host)
    {
        var value = host.Trim();
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("127.", StringComparison.Ordinal);
    }

    private static AgentControlResponse SendAgentControlRequest(string controlSocketPath, string messageType, IReadOnlyDictionary<string, object> payload)
    {
        const int maxAttempts = 12;
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(controlSocketPath))
                {
                    lastFailure = new IOException($"Control socket not found: {controlSocketPath}");
                    Thread.Sleep(100 * attempt);
                    continue;
                }

                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.ReceiveTimeout = DeployHandshakeTimeoutMs;
                socket.SendTimeout = DeployHandshakeTimeoutMs;
                socket.Connect(new UnixDomainSocketEndPoint(controlSocketPath));

                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                // The first control line is a greeting and may arrive before request/response exchange.
                _ = TryReadControlEnvelope(reader);

                var requestId = Guid.NewGuid().ToString("N");
                var request = new Dictionary<string, object?>
                {
                    ["v"] = "0.2",
                    ["id"] = requestId,
                    ["type"] = messageType,
                    ["payload"] = payload
                };

                writer.WriteLine(JsonSerializer.Serialize(request));

                while (true)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new InvalidOperationException($"Agent control socket returned empty response for {messageType}.");
                    }

                    var envelope = ParseControlEnvelope(line);
                    if (!string.Equals(envelope.Id, requestId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return envelope;
                }
            }
            catch (SocketException ex)
            {
                lastFailure = ex;
                if (attempt == maxAttempts)
                {
                    throw;
                }

                Thread.Sleep(100 * attempt);
            }
            catch (IOException ex) when (FindSocketException(ex) is not null)
            {
                lastFailure = ex;
                if (attempt == maxAttempts)
                {
                    throw;
                }

                Thread.Sleep(100 * attempt);
            }
        }

        throw new InvalidOperationException($"Failed {messageType} control request after retries.", lastFailure);
    }

    private static AgentControlResponse? TryReadControlEnvelope(StreamReader reader)
    {
        try
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            return ParseControlEnvelope(line);
        }
        catch
        {
            return null;
        }
    }

    private static AgentControlResponse ParseControlEnvelope(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(root, "type");
        var id = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(root, "id");

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return new AgentControlResponse(type, id, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var server = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "server");
        var message = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "message");
        var sessionId = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "session_id");
        var botId = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "bot_id");
        var controlToken = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "control_token");
        var ownerToken = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "owner_token");
        var dashboardEndpoint = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_endpoint");
        var dashboardHost = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_host");
        var dashboardPort = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_port");

        return new AgentControlResponse(type, id, message, server, sessionId, botId, controlToken, ownerToken, dashboardEndpoint, dashboardHost, dashboardPort);
    }

    private static string BuildAgentControlSocketPath(string botId)
    {
        var safe = new StringBuilder(botId.Length);
        foreach (var ch in botId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                safe.Append(ch);
            }
            else
            {
                safe.Append('_');
            }
        }

        if (safe.Length == 0)
        {
            safe.Append("bot");
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"bbs-agent-{safe}.sock");
        return socketPath + ".control";
    }

    private static void WaitForControlSocketReady(string controlSocketPath, int timeoutMs)
    {
        if (File.Exists(controlSocketPath))
        {
            return;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (File.Exists(controlSocketPath))
            {
                return;
            }

            Thread.Sleep(50);
        }
    }
}
