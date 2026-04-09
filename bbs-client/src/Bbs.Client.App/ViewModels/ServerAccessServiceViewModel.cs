using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Storage;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class ServerAccessServiceViewModel : ViewModelBase
{
    private readonly IClientStorage _storage;
    private readonly IClientLogger _logger;
    private readonly SessionServiceViewModel _sessionService;
    private readonly HttpClient _httpClient;

    private int _serverAccessRefreshVersion;
    private bool _isServerAccessLoading;
    private string _serverAccessStatus = "Select a server to load server access metadata.";
    private string _serverAccessOwnerToken = "-";
    private string _serverAccessDashboardEndpoint = "-";
    private string _ownerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";

    public ServerAccessServiceViewModel(
        IClientStorage storage,
        IClientLogger logger,
        SessionServiceViewModel sessionService,
        HttpClient httpClient)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public ServerAccessMetadata ServerAccessMetadata { get; set; } = ServerAccessMetadata.Invalid("No metadata loaded.");

    public bool IsServerAccessLoading
    {
        get => _isServerAccessLoading;
        set
        {
            if (_isServerAccessLoading == value)
            {
                return;
            }

            _isServerAccessLoading = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessStatus
    {
        get => _serverAccessStatus;
        set
        {
            if (_serverAccessStatus == value)
            {
                return;
            }

            _serverAccessStatus = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessOwnerToken
    {
        get => _serverAccessOwnerToken;
        set
        {
            if (_serverAccessOwnerToken == value)
            {
                return;
            }

            _serverAccessOwnerToken = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessDashboardEndpoint
    {
        get => _serverAccessDashboardEndpoint;
        set
        {
            if (_serverAccessDashboardEndpoint == value)
            {
                return;
            }

            _serverAccessDashboardEndpoint = value;
            OnPropertyChanged();
        }
    }

    public string OwnerTokenActionStatus
    {
        get => _ownerTokenActionStatus;
        set
        {
            if (_ownerTokenActionStatus == value)
            {
                return;
            }

            _ownerTokenActionStatus = value;
            OnPropertyChanged();
        }
    }

    public bool HasValidServerAccess => ServerAccessMetadata.IsValid;
    public bool ShowOwnerTokenActions => HasValidServerAccess;
    public bool ShowOwnerTokenActionsUnavailable => !ShowOwnerTokenActions;

    public async Task RefreshServerAccessMetadataAsync(ServerSummaryItem? selectedServer, string? selectedBotId, WorkspaceContext currentContext)
    {
        var refreshVersion = System.Threading.Interlocked.Increment(ref _serverAccessRefreshVersion);
        IsServerAccessLoading = true;
        ServerAccessStatus = "Refreshing server access metadata...";

        try
        {
            var resolved = await Task.Run(() => ResolveServerAccessMetadata(selectedServer, selectedBotId, currentContext));
            if (refreshVersion != System.Threading.Volatile.Read(ref _serverAccessRefreshVersion))
            {
                return;
            }

            ServerAccessMetadata = resolved;
            ServerAccessOwnerToken = resolved.IsValid ? MainWindowViewModelHelpers.MaskToken(resolved.OwnerToken) : "-";
            ServerAccessDashboardEndpoint = resolved.IsValid ? resolved.DashboardEndpoint : "-";
            ServerAccessStatus = resolved.StatusMessage;
            if (!resolved.IsValid)
            {
                OwnerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";
            }
        }
        catch
        {
            if (refreshVersion != System.Threading.Volatile.Read(ref _serverAccessRefreshVersion))
            {
                return;
            }

            ServerAccessMetadata = ServerAccessMetadata.Invalid("Failed to refresh server access metadata.");
            ServerAccessOwnerToken = "-";
            ServerAccessDashboardEndpoint = "-";
            ServerAccessStatus = "Failed to refresh server access metadata.";
            OwnerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";
        }
        finally
        {
            if (refreshVersion == System.Threading.Volatile.Read(ref _serverAccessRefreshVersion))
            {
                IsServerAccessLoading = false;
            }
        }
    }

    public async Task ExecuteOwnerTokenActionAsync(
        OwnerTokenActionType actionType,
        ServerSummaryItem? selectedServer,
        string ownerArenaSelectedPlugin,
        string ownerArenaArgs,
        string ownerArenaTimeMs,
        bool ownerArenaAllowHandicap,
        string ownerJoinArenaId,
        string ownerJoinHandicapPercent)
    {
        var selectedServerId = selectedServer?.ServerId;
        var guard = OwnerTokenGatedActionRules.Validate(actionType, ServerAccessMetadata, selectedServerId);
        if (!guard.CanExecute || guard.Plan is null)
        {
            OwnerTokenActionStatus = guard.Message;
            _logger.Log(LogLevel.Warning, "owner_token_action_blocked", "Owner-token action blocked by precondition check.",
                new Dictionary<string, string>
                {
                    ["action"] = actionType.ToString(),
                    ["reason"] = guard.Message
                });
            return;
        }

        if (!TryBuildOwnerActionFormFields(
                actionType,
            ServerAccessMetadata.OwnerToken,
                ownerArenaSelectedPlugin,
                ownerArenaArgs,
                ownerArenaTimeMs,
                ownerArenaAllowHandicap,
                ownerJoinArenaId,
                ownerJoinHandicapPercent,
                out var fields,
                out var validationError))
        {
            OwnerTokenActionStatus = validationError;
            return;
        }

        var actionUri = BuildDashboardActionUri(ServerAccessMetadata.DashboardEndpoint, guard.Plan.PlaceholderRoute);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, actionUri)
            {
                Content = new FormUrlEncodedContent(fields)
            };
            using var response = await _httpClient.SendAsync(request);
            var responseHtml = await response.Content.ReadAsStringAsync();
            var message = ExtractDashboardActionMessage(responseHtml);

            if (response.IsSuccessStatusCode)
            {
                OwnerTokenActionStatus = string.IsNullOrWhiteSpace(message)
                    ? $"{guard.Plan.DisplayName} request accepted."
                    : message;
                _logger.Log(LogLevel.Information, "owner_token_action_invoked", "Owner-token action invoked successfully.",
                    new Dictionary<string, string>
                    {
                        ["action"] = actionType.ToString(),
                        ["server_id"] = selectedServerId ?? "none",
                        ["route"] = guard.Plan.PlaceholderRoute
                    });
                return;
            }

            OwnerTokenActionStatus = string.IsNullOrWhiteSpace(message)
                ? $"{guard.Plan.DisplayName} failed with HTTP {(int)response.StatusCode}."
                : message;
        }
        catch (Exception ex)
        {
            OwnerTokenActionStatus = $"{guard.Plan.DisplayName} failed: {ex.Message}";
            _logger.Log(LogLevel.Warning, "owner_token_action_failed", "Owner-token action failed.",
                new Dictionary<string, string>
                {
                    ["action"] = actionType.ToString(),
                    ["server_id"] = selectedServerId ?? "none",
                    ["route"] = guard.Plan.PlaceholderRoute,
                    ["error"] = ex.GetType().Name
                });
        }
    }

    private ServerAccessMetadata ResolveServerAccessMetadata(ServerSummaryItem? selectedServer, string? selectedBotId, WorkspaceContext currentContext)
    {
        if (selectedServer is not null)
        {
            var knownServerAccess = ResolveKnownServerAccessMetadata(selectedServer);
            if (knownServerAccess.IsValid || currentContext == WorkspaceContext.ServerDetails)
            {
                return knownServerAccess;
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedBotId) &&
            _sessionService.TryGetActiveServerAccess(selectedBotId, selectedServer?.ServerId, out var activeServerId, out var activeAccess) &&
            (selectedServer is null || string.IsNullOrWhiteSpace(selectedServer.ServerId) || string.Equals(selectedServer.ServerId, activeServerId, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(activeAccess.OwnerToken))
            {
                return ServerAccessMetadata.Invalid("Owner token is not available for this runtime session yet.");
            }

            return activeAccess;
        }

        BotProfile? profile = null;
        AgentRuntimeState? runtimeState = null;

        if (!string.IsNullOrWhiteSpace(selectedBotId))
        {
            profile = _storage.ListBotProfilesAsync().GetAwaiter().GetResult().FirstOrDefault(b => b.BotId == selectedBotId);
            if (profile is not null)
            {
                runtimeState = _storage.GetAgentRuntimeStateAsync(profile.BotId).GetAwaiter().GetResult();
            }
        }

        if (profile is null)
        {
            var allProfiles = _storage.ListBotProfilesAsync().GetAwaiter().GetResult();
            foreach (var candidate in allProfiles)
            {
                var candidateState = _storage.GetAgentRuntimeStateAsync(candidate.BotId).GetAwaiter().GetResult();
                if (candidateState?.IsAttached == true)
                {
                    profile = candidate;
                    runtimeState = candidateState;
                    break;
                }
            }
        }

        return ServerAccessMetadataResolver.Resolve(profile, runtimeState, selectedServer?.ServerId);
    }

    private ServerAccessMetadata ResolveKnownServerAccessMetadata(ServerSummaryItem server)
    {
        var ownerToken = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(server.Metadata, new[]
        {
            "client.owner_token",
            "server_access.owner_token"
        });

        if (string.IsNullOrWhiteSpace(ownerToken))
        {
            return ServerAccessMetadata.Invalid("Owner token missing from selected server metadata.");
        }

        var dashboardEndpoint = ResolveServerDashboardEndpoint(server);
        if (string.IsNullOrWhiteSpace(dashboardEndpoint) || !Uri.TryCreate(dashboardEndpoint, UriKind.Absolute, out _))
        {
            return ServerAccessMetadata.Invalid("Dashboard endpoint missing or invalid for selected server.");
        }

        return new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: ownerToken,
            DashboardEndpoint: dashboardEndpoint,
            StatusMessage: "Server access metadata loaded from selected server profile.",
            Source: "known-server-metadata");
    }

    private static string ResolveServerDashboardEndpoint(ServerSummaryItem server)
    {
        var value = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(server.Metadata, new[] { "dashboard_endpoint" });
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var dashboardPort = MainWindowViewModelHelpers.ParsePositivePort(server.Metadata, new[] { "dashboard_port" }) ?? 3000;
        var scheme = server.UseTls ? "https" : "http";
        return MainWindowViewModelHelpers.BuildBaseEndpoint(scheme, server.Host, dashboardPort);
    }

    private static bool TryBuildOwnerActionFormFields(
        OwnerTokenActionType actionType,
        string ownerToken,
        string ownerArenaSelectedPlugin,
        string ownerArenaArgs,
        string ownerArenaTimeMs,
        bool ownerArenaAllowHandicap,
        string ownerJoinArenaId,
        string ownerJoinHandicapPercent,
        out IReadOnlyDictionary<string, string> fields,
        out string validationError)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner_token"] = ownerToken
        };

        if (actionType == OwnerTokenActionType.CreateArena)
        {
            if (string.IsNullOrWhiteSpace(ownerArenaSelectedPlugin))
            {
                fields = values;
                validationError = "Select a plugin before creating an arena.";
                return false;
            }

            values["game"] = ownerArenaSelectedPlugin.Trim();
            if (!string.IsNullOrWhiteSpace(ownerArenaArgs))
            {
                values["game_args"] = ownerArenaArgs.Trim();
            }

            if (!string.IsNullOrWhiteSpace(ownerArenaTimeMs))
            {
                values["time_ms"] = ownerArenaTimeMs.Trim();
            }

            values["allow_handicap"] = ownerArenaAllowHandicap ? "true" : "false";
            fields = values;
            validationError = string.Empty;
            return true;
        }

        if (!int.TryParse(ownerJoinArenaId.Trim(), out var arenaId) || arenaId <= 0)
        {
            fields = values;
            validationError = "Join Arena requires a positive arena ID.";
            return false;
        }

        if (!int.TryParse(ownerJoinHandicapPercent.Trim(), out var handicapPercent))
        {
            fields = values;
            validationError = "Join Arena handicap must be an integer.";
            return false;
        }

        values["arena_id"] = arenaId.ToString();
        values["handicap_percent"] = handicapPercent.ToString();
        fields = values;
        validationError = string.Empty;
        return true;
    }

    private static Uri BuildDashboardActionUri(string dashboardEndpoint, string relativeRoute)
    {
        var endpoint = new Uri(dashboardEndpoint, UriKind.Absolute);
        var builder = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port)
        {
            Path = relativeRoute
        };
        return builder.Uri;
    }

    private static string ExtractDashboardActionMessage(string responseHtml)
    {
        if (string.IsNullOrWhiteSpace(responseHtml))
        {
            return string.Empty;
        }

        var start = responseHtml.IndexOf('>');
        var end = responseHtml.LastIndexOf('<');
        if (start >= 0 && end > start)
        {
            return responseHtml[(start + 1)..end].Trim();
        }

        return responseHtml.Trim();
    }
}