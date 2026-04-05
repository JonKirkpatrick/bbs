using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Windows.Input;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Personas;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int ServerProbeTimeoutMs = 1200;
    private const int ServerCatalogFetchTimeoutMs = 2000;
    private const int ServerCatalogSelectionRefreshCooldownMs = 5000;
    private const int DashboardPortFallback = 3000;
    private const int BotTcpDefaultPort = 8080;
    private const int ServerProbeMaxAttempts = 2;
    private const int ServerProbeRetryDelayMs = 200;
    private const int DeployHandshakeTimeoutMs = 3000;
    private const int DeployControlSocketReadyTimeoutMs = 8000;
    private const string ProbeStatusMetadataKey = "probe_status";
    private const string ProbeLastCheckedMetadataKey = "probe_last_checked_utc";
    private const string ProbeLastErrorMetadataKey = "probe_last_error";
    private const string ServerAccessServerIdMetadataKey = "server_access.server_id";
    private const string ServerAccessSessionIdMetadataKey = "server_access.session_id";
    private const string ServerAccessOwnerTokenMetadataKey = "server_access.owner_token";
    private const string ServerAccessDashboardEndpointMetadataKey = "server_access.dashboard_endpoint";
    private const string ClientOwnerTokenMetadataKey = "client.owner_token";
    private static readonly string[] DashboardEndpointMetadataKeys =
    {
        "dashboard_endpoint",
        "server.dashboard_endpoint",
        "server_access.dashboard_endpoint"
    };

    private static readonly string[] DashboardPortMetadataKeys =
    {
        "dashboard_port",
        "server.dashboard_port"
    };

    private static readonly string[] ServerGlobalIdMetadataKeys =
    {
        "global_server_id",
        "server.global_server_id",
        "server_identity.global_server_id"
    };

    private readonly IClientLogger _logger;
    private readonly PersonaManager? _personaManager;
    private string? _currentPersonaPath;
    private IClientStorage _storage;
    private IBotOrchestrationService _orchestration;
    private readonly HttpClient _serverCatalogHttpClient;
    private WorkspaceContext _currentContext;
    private BotSummaryItem? _selectedBot;
    private ServerSummaryItem? _selectedServer;
    private bool _isLeftPanelExpanded = true;
    private bool _isRightPanelExpanded = true;
    private string _botEditorName = string.Empty;
    private string _botEditorLaunchPath = string.Empty;
    private string _botEditorArgs = string.Empty;
    private string _botEditorMetadata = string.Empty;
    private string _botEditorMessage = "Fill out the bot form and save.";
    private string _serverEditorName = string.Empty;
    private string _serverEditorHost = string.Empty;
    private string _serverEditorPort = "3000";
    private bool _serverEditorUseTls;
    private string _serverEditorMetadata = string.Empty;
    private string _serverEditorMessage = "Fill out the server form and save.";
    private bool _isServerProbeInProgress;
    private bool _isServerDetailLoading;
    private bool _isServerAccessLoading;
    private string _serverCatalogStatus = "Select a server to view cached plugin catalog.";
    private string _serverAccessStatus = "Select a server to load server access metadata.";
    private string _ownerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";
    private string _ownerArenaSelectedPlugin = string.Empty;
    private string _ownerArenaArgs = string.Empty;
    private string _ownerArenaTimeMs = string.Empty;
    private bool _ownerArenaAllowHandicap = true;
    private string _ownerJoinArenaId = string.Empty;
    private string _ownerJoinHandicapPercent = "0";
    private string _serverAccessOwnerToken = "-";
    private string _serverAccessDashboardEndpoint = "-";
    private int _serverAccessRefreshVersion;
    private readonly Dictionary<string, DateTimeOffset> _serverCatalogLastRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _serverCatalogRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _serverProbeLock = new();
    private readonly object _serverCatalogRefreshLock = new();
    private readonly object _deployConnectionLock = new();
    private readonly HashSet<(string BotId, string SessionId)> _activeDeployConnections = new();
    private readonly object _activeAccessCacheLock = new();
    private readonly Dictionary<(string BotId, string SessionId), (string RuntimeBotId, string RuntimeBotName, string ServerId, ServerAccessMetadata Access)> _activeSessionsByBotAndSession = new();

    private MainWindowViewModel(
        IClientLogger logger,
        IClientStorage storage,
        IBotOrchestrationService orchestration,
        bool embeddedViewerAvailable = true,
        string? embeddedViewerMessage = null)
    {
        _logger = logger;
        _storage = storage;
        _orchestration = orchestration;
        _serverCatalogHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(ServerCatalogFetchTimeoutMs)
        };
        Bots = new ObservableCollection<BotSummaryItem>();
        Servers = new ObservableCollection<ServerSummaryItem>();
        ServerMetadataEntries = new ObservableCollection<ServerMetadataEntryItem>();
        ServerPluginCatalogEntries = new ObservableCollection<ServerPluginCatalogItem>();
        ServerArenaEntries = new ObservableCollection<ServerArenaItem>();

        ToggleLeftPanelCommand = new RelayCommand(ToggleLeftPanel);
        ToggleRightPanelCommand = new RelayCommand(ToggleRightPanel);
        SetHomeContextCommand = new RelayCommand(SetHomeContext);
        SetBotContextCommand = new RelayCommand(SetBotContextFromSelection, () => SelectedBot is not null && IsPersonaLoaded);
        SetServerContextCommand = new RelayCommand(SetServerContextFromSelection, () => SelectedServer is not null && IsPersonaLoaded);
        SaveBotProfileCommand = new RelayCommand(SaveBotProfile);
        StartNewBotCommand = new RelayCommand(StartNewBot);
        DeploySelectedBotCommand = new RelayCommand(DeploySelectedBotToSelectedServer, CanDeploySelectedBot);
        SaveServerProfileCommand = new RelayCommand(SaveServerProfile);
        StartNewServerCommand = new RelayCommand(StartNewServer);
        ReprobeServersCommand = new RelayCommand(ReprobeServers, () => !IsServerProbeInProgress && Servers.Count > 0 && IsPersonaLoaded);
        RefreshServerAccessCommand = new RelayCommand(RefreshServerAccessMetadata);
        CreateArenaCommand = new RelayCommand(ExecuteCreateArena, CanExecuteOwnerTokenAction);
        JoinArenaCommand = new RelayCommand(ExecuteJoinArena, CanExecuteOwnerTokenAction);
        RefreshServerArenasCommand = new RelayCommand(RefreshServerArenas, () => SelectedServer is not null && IsPersonaLoaded);
        OpenArenaViewerInBrowserCommand = new RelayCommand(OpenArenaViewerInBrowser, () => !string.IsNullOrWhiteSpace(ArenaViewerUrl));

        ConfigureEmbeddedViewerSupport(embeddedViewerAvailable, embeddedViewerMessage);

        _logger.Log(LogLevel.Information, "mainvm_init_mvfix", "MainWindowViewModel constructor initialized (mvfix path).", null);

        _currentContext = WorkspaceContext.Home;
        // Do NOT load bots/servers here; wait for persona to be loaded
    }

    public string WorkspaceTitle { get; private set; } = "";
    public string WorkspaceDescription { get; private set; } = "";

    public string CurrentContextLabel => $"Context: {_currentContext}";

    public string CurrentTitleText => _currentContext switch
    {
        WorkspaceContext.BotDetails => SelectedBot is null ? "Bot Context" : $"{SelectedBot.Name}",
        WorkspaceContext.ServerDetails => SelectedServer is null ? "Server Context" : $"{SelectedServer.Name}",
        WorkspaceContext.ArenaViewer => string.IsNullOrWhiteSpace(ArenaViewerLabel) ? "Arena Viewer" : ArenaViewerLabel,
        _ => "BBS"
    };
    public bool ShowBotEditor => _currentContext == WorkspaceContext.BotDetails;
    public bool ShowServerEditor => _currentContext == WorkspaceContext.ServerDetails;
    public bool ShowArenaViewer => _currentContext == WorkspaceContext.ArenaViewer;
    public bool IsServerDetailLoading
    {
        get => _isServerDetailLoading;
        private set
        {
            if (_isServerDetailLoading == value)
            {
                return;
            }

            _isServerDetailLoading = value;
            OnPropertyChanged();
        }
    }

    public bool IsServerAccessLoading
    {
        get => _isServerAccessLoading;
        private set
        {
            if (_isServerAccessLoading == value)
            {
                return;
            }

            _isServerAccessLoading = value;
            OnPropertyChanged();
            ((RelayCommand)CreateArenaCommand).RaiseCanExecuteChanged();
            ((RelayCommand)JoinArenaCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedServer => SelectedServer is not null;
    public bool HasServerMetadata => ServerMetadataEntries.Count > 0;
    public bool HasServerPluginCatalog => ServerPluginCatalogEntries.Count > 0;
    public bool ShowServerMetadataEmpty => !IsServerDetailLoading && !HasServerMetadata;
    public bool ShowServerPluginCatalogEmpty => !IsServerDetailLoading && !HasServerPluginCatalog;
    public bool HasValidServerAccess => ServerAccessMetadata.IsValid;
    public bool ShowOwnerTokenActions => HasValidServerAccess;
    public bool ShowOwnerTokenActionsUnavailable => !ShowOwnerTokenActions;
    public string ServerDetailEndpoint => SelectedServer?.Endpoint ?? "-";
    public string ServerDetailProbeStatus => SelectedServer?.Status ?? "Status: not available";
    public string ServerAccessStatus
    {
        get => _serverAccessStatus;
        private set
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
        private set
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
        private set
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
        private set
        {
            if (_ownerTokenActionStatus == value)
            {
                return;
            }

            _ownerTokenActionStatus = value;
            OnPropertyChanged();
        }
    }

    public string OwnerArenaSelectedPlugin
    {
        get => _ownerArenaSelectedPlugin;
        set
        {
            if (string.Equals(_ownerArenaSelectedPlugin, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaSelectedPlugin = value;
            OnPropertyChanged();
            SyncOwnerArenaArgsFromSelectedPlugin();
        }
    }

    public string OwnerArenaArgs
    {
        get => _ownerArenaArgs;
        set
        {
            if (string.Equals(_ownerArenaArgs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaArgs = value;
            OnPropertyChanged();
        }
    }

    public string OwnerArenaTimeMs
    {
        get => _ownerArenaTimeMs;
        set
        {
            if (string.Equals(_ownerArenaTimeMs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaTimeMs = value;
            OnPropertyChanged();
        }
    }

    public bool OwnerArenaAllowHandicap
    {
        get => _ownerArenaAllowHandicap;
        set
        {
            if (_ownerArenaAllowHandicap == value)
            {
                return;
            }

            _ownerArenaAllowHandicap = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinArenaId
    {
        get => _ownerJoinArenaId;
        set
        {
            if (string.Equals(_ownerJoinArenaId, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinArenaId = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinHandicapPercent
    {
        get => _ownerJoinHandicapPercent;
        set
        {
            if (string.Equals(_ownerJoinHandicapPercent, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinHandicapPercent = value;
            OnPropertyChanged();
        }
    }

    public ServerAccessMetadata ServerAccessMetadata { get; private set; } = ServerAccessMetadata.Invalid("No metadata loaded.");
    public string ServerCatalogStatus
    {
        get => _serverCatalogStatus;
        private set
        {
            if (_serverCatalogStatus == value)
            {
                return;
            }

            _serverCatalogStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
        }
    }
    public bool IsServerProbeInProgress
    {
        get => _isServerProbeInProgress;
        private set
        {
            if (_isServerProbeInProgress == value)
            {
                return;
            }

            _isServerProbeInProgress = value;
            OnPropertyChanged();
            ((RelayCommand)ReprobeServersCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsLeftPanelExpanded
    {
        get => _isLeftPanelExpanded;
        private set
        {
            if (_isLeftPanelExpanded == value)
            {
                return;
            }

            _isLeftPanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLeftPanelCollapsed));
            OnPropertyChanged(nameof(LeftPanelWidth));
        }
    }

    public bool IsRightPanelExpanded
    {
        get => _isRightPanelExpanded;
        private set
        {
            if (_isRightPanelExpanded == value)
            {
                return;
            }

            _isRightPanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRightPanelCollapsed));
            OnPropertyChanged(nameof(RightPanelWidth));
        }
    }

    public bool IsLeftPanelCollapsed => !IsLeftPanelExpanded;
    public bool IsRightPanelCollapsed => !IsRightPanelExpanded;

    public GridLength LeftPanelWidth => IsLeftPanelExpanded ? new GridLength(280) : new GridLength(56);
    public GridLength RightPanelWidth => IsRightPanelExpanded ? new GridLength(280) : new GridLength(56);

    public ObservableCollection<BotSummaryItem> Bots { get; }
    public ObservableCollection<ServerSummaryItem> Servers { get; }
    public ObservableCollection<ServerMetadataEntryItem> ServerMetadataEntries { get; }
    public ObservableCollection<ServerPluginCatalogItem> ServerPluginCatalogEntries { get; }
    public ObservableCollection<ServerArenaItem> ServerArenaEntries { get; }

    public string BotEditorName
    {
        get => _botEditorName;
        set
        {
            if (_botEditorName == value)
            {
                return;
            }

            _botEditorName = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorLaunchPath
    {
        get => _botEditorLaunchPath;
        set
        {
            if (_botEditorLaunchPath == value)
            {
                return;
            }

            _botEditorLaunchPath = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorArgs
    {
        get => _botEditorArgs;
        set
        {
            if (_botEditorArgs == value)
            {
                return;
            }

            _botEditorArgs = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorMetadata
    {
        get => _botEditorMetadata;
        set
        {
            if (_botEditorMetadata == value)
            {
                return;
            }

            _botEditorMetadata = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorMessage
    {
        get => _botEditorMessage;
        private set
        {
            if (_botEditorMessage == value)
            {
                return;
            }

            _botEditorMessage = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorName
    {
        get => _serverEditorName;
        set
        {
            if (_serverEditorName == value)
            {
                return;
            }

            _serverEditorName = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorHost
    {
        get => _serverEditorHost;
        set
        {
            if (_serverEditorHost == value)
            {
                return;
            }

            _serverEditorHost = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorPort
    {
        get => _serverEditorPort;
        set
        {
            if (_serverEditorPort == value)
            {
                return;
            }

            _serverEditorPort = value;
            OnPropertyChanged();
        }
    }

    public bool ServerEditorUseTls
    {
        get => _serverEditorUseTls;
        set
        {
            if (_serverEditorUseTls == value)
            {
                return;
            }

            _serverEditorUseTls = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMetadata
    {
        get => _serverEditorMetadata;
        set
        {
            if (_serverEditorMetadata == value)
            {
                return;
            }

            _serverEditorMetadata = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMessage
    {
        get => _serverEditorMessage;
        private set
        {
            if (_serverEditorMessage == value)
            {
                return;
            }

            _serverEditorMessage = value;
            OnPropertyChanged();
        }
    }

    public BotSummaryItem? SelectedBot
    {
        get => _selectedBot;
        set
        {
            if (_selectedBot == value)
            {
                return;
            }

            _selectedBot = value;
            OnPropertyChanged();
            ((RelayCommand)SetBotContextCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                PopulateBotEditor(value);
                _currentContext = WorkspaceContext.BotDetails; 
                RefreshContextProjection();
            }
            RefreshActiveBotSessionsProjection();
            TriggerServerAccessRefresh();
        }
    }

    public ServerSummaryItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer == value)
            {
                return;
            }

            _selectedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
            RefreshSelectedServerDetail();
            ((RelayCommand)SetServerContextCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RefreshServerArenasCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                StopArenaViewerWatch();
                _currentContext = WorkspaceContext.ServerDetails;
                PopulateServerEditor(value);
                RefreshContextProjection();
            }

            RefreshActiveBotSessionsProjection();
            TriggerServerAccessRefresh();
        }
    }

    public ICommand ToggleLeftPanelCommand { get; }
    public ICommand ToggleRightPanelCommand { get; }
    public ICommand SetHomeContextCommand { get; }
    public ICommand SetBotContextCommand { get; }
    public ICommand SetServerContextCommand { get; }
    public ICommand SaveBotProfileCommand { get; }
    public ICommand StartNewBotCommand { get; }
    public ICommand DeploySelectedBotCommand { get; }
    public ICommand SaveServerProfileCommand { get; }
    public ICommand StartNewServerCommand { get; }
    public ICommand ReprobeServersCommand { get; }
    public ICommand RefreshServerAccessCommand { get; }
    public ICommand CreateArenaCommand { get; }
    public ICommand JoinArenaCommand { get; }
    public ICommand RefreshServerArenasCommand { get; }
    public ICommand OpenArenaViewerInBrowserCommand { get; }

    private void ToggleLeftPanel()
    {
        IsLeftPanelExpanded = !IsLeftPanelExpanded;
    }

    private void ToggleRightPanel()
    {
        IsRightPanelExpanded = !IsRightPanelExpanded;
    }

    private void ReprobeServers()
    {
        _ = RunServerProbeCycleAsync(trigger: "manual", updateEditorStatus: true);
    }

    private void RefreshServerAccessMetadata()
    {
        TriggerServerAccessRefresh();
    }

    private bool CanExecuteOwnerTokenAction()
    {
        return HasValidServerAccess && !IsServerAccessLoading && SelectedServer is not null;
    }

    private bool CanDeploySelectedBot()
    {
        if (!IsPersonaLoaded || _currentContext != WorkspaceContext.ServerDetails)
        {
            return false;
        }

        if (SelectedBot is null || SelectedServer is null)
        {
            return false;
        }

        return SelectedServer.VisualState == ServerCardVisualState.Live;
    }

    private void ExecuteCreateArena()
    {
        _ = ExecuteOwnerTokenActionAsync(OwnerTokenActionType.CreateArena);
    }

    private void ExecuteJoinArena()
    {
        _ = ExecuteOwnerTokenActionAsync(OwnerTokenActionType.JoinArena);
    }

    private async Task ExecuteOwnerTokenActionAsync(OwnerTokenActionType actionType)
    {
        var selectedServerId = SelectedServer?.ServerId;
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

        if (!TryBuildOwnerActionFormFields(actionType, out var fields, out var validationError))
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
            using var response = await _serverCatalogHttpClient.SendAsync(request);
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

    private bool TryBuildOwnerActionFormFields(OwnerTokenActionType actionType, out IReadOnlyDictionary<string, string> fields, out string validationError)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner_token"] = ServerAccessMetadata.OwnerToken
        };

        if (actionType == OwnerTokenActionType.CreateArena)
        {
            if (string.IsNullOrWhiteSpace(OwnerArenaSelectedPlugin))
            {
                fields = values;
                validationError = "Select a plugin before creating an arena.";
                return false;
            }

            values["game"] = OwnerArenaSelectedPlugin.Trim();
            if (!string.IsNullOrWhiteSpace(OwnerArenaArgs))
            {
                values["game_args"] = OwnerArenaArgs.Trim();
            }

            if (!string.IsNullOrWhiteSpace(OwnerArenaTimeMs))
            {
                values["time_ms"] = OwnerArenaTimeMs.Trim();
            }

            values["allow_handicap"] = OwnerArenaAllowHandicap ? "true" : "false";
            fields = values;
            validationError = string.Empty;
            return true;
        }

        if (!int.TryParse(OwnerJoinArenaId.Trim(), out var arenaId) || arenaId <= 0)
        {
            fields = values;
            validationError = "Join Arena requires a positive arena ID.";
            return false;
        }

        if (!int.TryParse(OwnerJoinHandicapPercent.Trim(), out var handicapPercent))
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

    private void SetHomeContext()
    {
        StopArenaViewerWatch();
        _currentContext = WorkspaceContext.Home;
        RefreshContextProjection();
    }

    private void SetBotContextFromSelection()
    {
        StopArenaViewerWatch();
        _currentContext = WorkspaceContext.BotDetails;
        RefreshContextProjection();
    }

    private void SetServerContextFromSelection()
    {
        StopArenaViewerWatch();
        _currentContext = WorkspaceContext.ServerDetails;
        RefreshContextProjection();
    }

    private void RefreshContextProjection()
    {
        switch (_currentContext)
        {
            case WorkspaceContext.BotDetails:
                WorkspaceTitle = SelectedBot is null ? "Bot Registration" : $"Bot: {SelectedBot.Name}";
                WorkspaceDescription = SelectedBot is null
                    ? "Use the form to register a new bot profile."
                    : "Edit and save this bot profile from the center workspace.";
                break;
            case WorkspaceContext.ServerDetails:
                WorkspaceTitle = SelectedServer is null ? "Server Context" : $"Server: {SelectedServer.Name}";
                WorkspaceDescription = SelectedServer is null
                    ? "Select a server card to load server context."
                    : $"{SelectedServer.Endpoint} | {SelectedServer.Status} | Plugins: {SelectedServer.PluginCount}";
                break;
            case WorkspaceContext.ArenaViewer:
                WorkspaceTitle = string.IsNullOrWhiteSpace(ArenaViewerLabel) ? "Arena Viewer" : ArenaViewerLabel;
                WorkspaceDescription = string.IsNullOrWhiteSpace(ArenaViewerStatus)
                    ? "Live arena watcher is active."
                    : ArenaViewerStatus;
                break;
            default:
                WorkspaceTitle = "";
                WorkspaceDescription = "";
                break;
        }

        OnPropertyChanged(nameof(CurrentContextLabel));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
        OnPropertyChanged(nameof(ShowBotEditor));
        OnPropertyChanged(nameof(ShowServerEditor));
        OnPropertyChanged(nameof(ShowArenaViewer));
        OnPropertyChanged(nameof(CurrentTitleText));
        ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenArenaViewerInBrowserCommand).RaiseCanExecuteChanged();
    }

    private void StartNewBot()
    {
        SelectedBot = null;
        _currentContext = WorkspaceContext.BotDetails;
        BotEditorName = string.Empty;
        BotEditorLaunchPath = string.Empty;
        BotEditorArgs = string.Empty;
        BotEditorMetadata = string.Empty;
        BotEditorMessage = "Creating a new bot profile.";
        RefreshContextProjection();
    }

    private void StartNewServer()
    {
        StopArenaViewerWatch();
        SelectedServer = null;
        _currentContext = WorkspaceContext.ServerDetails;
        ServerEditorName = string.Empty;
        ServerEditorHost = string.Empty;
        ServerEditorPort = "3000";
        ServerEditorUseTls = false;
        ServerEditorMetadata = string.Empty;
        ServerEditorMessage = "Creating a new known server.";
        RefreshContextProjection();
    }

    private void SaveBotProfile()
    {
        var botId = SelectedBot?.BotId ?? Guid.NewGuid().ToString("N");
        var createdAt = SelectedBot?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.UtcNow;

        var profile = BotProfile.Create(
            botId: botId,
            name: BotEditorName.Trim(),
            launchPath: BotEditorLaunchPath.Trim(),
            avatarImagePath: SelectedBot?.AvatarImagePath,
            launchArgs: MainWindowViewModelHelpers.ParseArgs(BotEditorArgs),
            metadata: MainWindowViewModelHelpers.ParseMetadata(BotEditorMetadata),
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            BotEditorMessage = $"Cannot save bot: {string.Join(", ", errors)}";
            _logger.Log(LogLevel.Warning, "bot_profile_validation_failed", "Bot profile save validation failed.",
                new Dictionary<string, string>
                {
                    ["errors"] = string.Join(",", errors)
                });
            return;
        }

        _storage.UpsertBotProfileAsync(profile).GetAwaiter().GetResult();
        LoadBotsFromStorage();
        SelectedBot = FindBotById(botId);
        BotEditorMessage = $"Saved bot profile: {profile.Name}";
        _logger.Log(LogLevel.Information, "bot_profile_saved", "Bot profile persisted.",
            new Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["name"] = profile.Name
            });
    }

    private void DeploySelectedBotToSelectedServer()
    {
        if (!CanDeploySelectedBot())
        {
            BotEditorMessage = "Deploy is only available when a live server is selected in Server context.";
            return;
        }

        var bot = SelectedBot;
        var server = SelectedServer;
        if (bot is null)
        {
            BotEditorMessage = "Select a bot before deploy.";
            return;
        }

        if (server is null)
        {
            BotEditorMessage = "Deploy requires a selected server.";
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

            lock (_deployConnectionLock)
            {
                _activeDeployConnections.Add((sourceProfile.BotId, registerResponse.SessionId));
            }

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
            BotEditorMessage = $"Deployed {sourceProfile.Name} to {server.Name}; active session established.";

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

            HandleOrchestrationException("deploy", bot.BotId, "deploy_attach_failed_mvfix", ex);
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
        var serverBotId = accessReply.BotId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(serverBotId))
        {
            throw new InvalidOperationException("Agent did not return a valid session_id/bot_id after server_connect.");
        }

        return new RegisterHandshakeResult(
            SessionId: sessionId,
            ServerBotId: serverBotId,
            ServerBotSecret: accessReply.ControlToken,
            OwnerToken: accessReply.OwnerToken,
            DashboardEndpoint: NormalizeDashboardEndpoint(accessReply.DashboardEndpoint, accessReply.DashboardHost, accessReply.DashboardPort, server.UseTls));
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
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? string.Empty : string.Empty;
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return new AgentControlResponse(type, id, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var message = payload.TryGetProperty("message", out var messageNode) ? messageNode.ToString() : string.Empty;
        var sessionId = payload.TryGetProperty("session_id", out var sessionIdNode) ? sessionIdNode.ToString() : string.Empty;
        var botId = payload.TryGetProperty("bot_id", out var botIdNode) ? botIdNode.ToString() : string.Empty;
        var controlToken = payload.TryGetProperty("control_token", out var controlTokenNode) ? controlTokenNode.ToString() : string.Empty;
        var ownerToken = payload.TryGetProperty("owner_token", out var ownerTokenNode) ? ownerTokenNode.ToString() : string.Empty;
        var dashboardEndpoint = payload.TryGetProperty("dashboard_endpoint", out var endpointNode) ? endpointNode.ToString() : string.Empty;
        var dashboardHost = payload.TryGetProperty("dashboard_host", out var hostNode) ? hostNode.ToString() : string.Empty;
        var dashboardPort = payload.TryGetProperty("dashboard_port", out var portNode) ? portNode.ToString() : string.Empty;

        return new AgentControlResponse(type, id, message, sessionId, botId, controlToken, ownerToken, dashboardEndpoint, dashboardHost, dashboardPort);
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

    public int ShutdownRuntimeSessionsForExit()
    {
        StopArenaViewerWatch();

        List<(string BotId, string SessionId)> sessions;
        lock (_deployConnectionLock)
        {
            sessions = _activeDeployConnections.ToList();
        }

        foreach (var session in sessions)
        {
            try
            {
                DisconnectActiveDeploymentConnection(session.BotId, session.SessionId, sendQuit: true);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, "shutdown_session_quit_failed", "Failed to quit session during app shutdown.",
                    new Dictionary<string, string>
                    {
                        ["bot_id"] = session.BotId,
                        ["session_id"] = session.SessionId,
                        ["error"] = ex.GetType().Name
                    });
            }
        }

        return sessions.Count;
    }

    private static string? ResolveServerGlobalId(ServerSummaryItem server)
    {
        var raw = FirstNonEmptyMetadataValue(server.Metadata, ServerGlobalIdMetadataKeys);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static SocketException? FindSocketException(Exception ex)
    {
        if (ex is SocketException directSocket)
        {
            return directSocket;
        }

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                var nested = FindSocketException(inner);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        if (ex.InnerException is not null)
        {
            return FindSocketException(ex.InnerException);
        }

        return null;
    }

    private static bool TryResolveSocketErrorCode(Exception ex, out string socketErrorCode)
    {
        var directSocket = FindSocketException(ex);
        if (directSocket is not null)
        {
            socketErrorCode = $"socket_{directSocket.SocketErrorCode}".ToLowerInvariant();
            return true;
        }

        var inspected = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<Exception>();
        pending.Enqueue(ex);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!inspected.Add(current))
            {
                continue;
            }

            var type = current.GetType();
            if (type.Name.Contains("SocketException", StringComparison.OrdinalIgnoreCase))
            {
                var socketCodeProperty = type.GetProperty("SocketErrorCode");
                if (socketCodeProperty?.GetValue(current) is { } codeValue)
                {
                    socketErrorCode = $"socket_{codeValue}".ToLowerInvariant();
                    return true;
                }

                socketErrorCode = "socket_error";
                return true;
            }

            if (current is IOException)
            {
                socketErrorCode = "socket_io_error";
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        socketErrorCode = string.Empty;
        return false;
    }

    private static string NormalizeDashboardEndpoint(string rawEndpoint, string rawHost, string rawPort, bool useTls)
    {
        var scheme = useTls ? "https" : "http";
        if (Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (!string.IsNullOrWhiteSpace(rawEndpoint))
        {
            var withScheme = $"{scheme}://{rawEndpoint}";
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out absolute))
            {
                return absolute.ToString();
            }
        }

        if (!string.IsNullOrWhiteSpace(rawHost) && !string.IsNullOrWhiteSpace(rawPort))
        {
            var withScheme = $"{scheme}://{rawHost}:{rawPort}";
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out absolute))
            {
                return absolute.ToString();
            }
        }

        return string.Empty;
    }

    private bool HasActiveDeployConnection(string botId)
    {
        lock (_deployConnectionLock)
        {
            return _activeDeployConnections.Any(session => session.BotId == botId);
        }
    }

    private void DisconnectActiveDeploymentConnection(string botId, string sessionId, bool sendQuit)
    {
        if (sendQuit)
        {
            TrySendQuitSession(botId, sessionId);
        }

        lock (_deployConnectionLock)
        {
            _activeDeployConnections.Remove((botId, sessionId));
        }

        ClearActiveServerAccess(botId, sessionId);
        RefreshActiveBotSessionsProjection();
        TriggerServerAccessRefresh();
    }

    private void DisconnectAllActiveDeploymentConnectionsForBot(string botId, bool sendQuit)
    {
        List<string> sessionsToRemove;
        lock (_deployConnectionLock)
        {
            sessionsToRemove = _activeDeployConnections
                .Where(session => session.BotId == botId)
                .Select(session => session.SessionId)
                .ToList();
        }

        foreach (var sessionId in sessionsToRemove)
        {
            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit);
        }
    }

    private void TrySendQuitSession(string sourceBotId, string sessionId)
    {
        try
        {
            if (!TryGetRuntimeSession(sourceBotId, sessionId, out var runtimeBotId, out _, out _))
            {
                return;
            }

            var controlSocketPath = BuildAgentControlSocketPath(runtimeBotId);
            if (!File.Exists(controlSocketPath))
            {
                return;
            }

            var reply = SendAgentControlRequest(controlSocketPath, "quit_session", new Dictionary<string, object>());
            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log(LogLevel.Warning, "session_quit_control_error", "Failed to quit active session via control socket.",
                    new Dictionary<string, string>
                    {
                        ["bot_id"] = sourceBotId,
                        ["runtime_bot_id"] = runtimeBotId,
                        ["session_id"] = sessionId,
                        ["message"] = reply.Message
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "session_quit_failed", "Failed to quit active session via control socket.",
                new Dictionary<string, string>
                {
                    ["bot_id"] = sourceBotId,
                    ["session_id"] = sessionId,
                    ["error"] = ex.GetType().Name
                });
        }
    }

    private void RefreshActiveBotSessionsProjection()
    {
        PruneStaleActiveSessionCaches();

        var selectedBot = SelectedBot;
        var selectedBotId = selectedBot?.BotId;
        ActiveBotSessions.Clear();

        if (selectedBot is null || string.IsNullOrWhiteSpace(selectedBotId))
        {
            OnPropertyChanged(nameof(HasActiveBotSessions));
            OnPropertyChanged(nameof(ShowActiveBotSessionsEmpty));
            return;
        }

        ReconcileActiveSessionsFromRuntimeProfiles(selectedBot);
        ReconcileActiveSessionsFromRuntimeSockets(selectedBot);

        List<(string BotId, string SessionId)> sessions;
        lock (_deployConnectionLock)
        {
            sessions = _activeDeployConnections
                .Where(s => string.Equals(s.BotId, selectedBotId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var session in sessions)
        {
            var serverId = string.Empty;
            var runtimeBotId = session.BotId;
            var runtimeBotName = session.BotId;
            var access = ServerAccessMetadata.Invalid("Owner token is not available for this server yet.");
            lock (_activeAccessCacheLock)
            {
                if (_activeSessionsByBotAndSession.TryGetValue((session.BotId, session.SessionId), out var cached))
                {
                    runtimeBotId = cached.RuntimeBotId;
                    runtimeBotName = cached.RuntimeBotName;
                    serverId = cached.ServerId;
                    access = cached.Access;
                }
            }

            var serverName = ResolveServerName(serverId);
            var arenaOptions = BuildArenaOptionsForServer(serverId);
            ActiveBotSessions.Add(new ActiveBotSessionItem
            {
                RuntimeBotId = runtimeBotId,
                SessionId = session.SessionId,
                ServerId = serverId,
                ServerName = string.IsNullOrWhiteSpace(runtimeBotName) ? serverName : $"{serverName} ({runtimeBotName})",
                OwnerTokenMasked = access.IsValid ? MainWindowViewModelHelpers.MaskToken(access.OwnerToken) : "-",
                ArenaOptions = arenaOptions,
                JoinCommand = new RelayCommand(() => ExecuteSessionJoin(session.BotId, session.SessionId)),
                LeaveCommand = new RelayCommand(() => ExecuteSessionLeave(session.BotId, session.SessionId)),
                QuitCommand = new RelayCommand(() => ExecuteSessionQuit(session.BotId, session.SessionId))
            });
        }

        OnPropertyChanged(nameof(HasActiveBotSessions));
        OnPropertyChanged(nameof(ShowActiveBotSessionsEmpty));
    }

    private void ReconcileActiveSessionsFromRuntimeProfiles(BotSummaryItem sourceBot)
    {
        var sourceBotId = sourceBot.BotId;
        var sourceBotName = sourceBot.Name;
        var profiles = _storage.ListBotProfilesAsync().GetAwaiter().GetResult();
        foreach (var profile in profiles)
        {
            if (!IsRuntimeInstanceProfile(profile))
            {
                continue;
            }

            var matchesSourceBotId = profile.Metadata.TryGetValue("source_bot_id", out var mappedSourceBotId) &&
                                     string.Equals(mappedSourceBotId, sourceBotId, StringComparison.OrdinalIgnoreCase);
            var matchesSourceBotName = profile.Metadata.TryGetValue("source_bot_name", out var mappedSourceBotName) &&
                                       string.Equals(mappedSourceBotName, sourceBotName, StringComparison.OrdinalIgnoreCase);
            var matchesRuntimeNamePrefix = profile.Name.StartsWith(sourceBotName + "-", StringComparison.OrdinalIgnoreCase);

            if (!matchesSourceBotId && !matchesSourceBotName && !matchesRuntimeNamePrefix)
            {
                continue;
            }

            var runtimeState = _storage.GetAgentRuntimeStateAsync(profile.BotId).GetAwaiter().GetResult();
            var runtimeSocketAlive = File.Exists(BuildAgentControlSocketPath(profile.BotId));
            if ((runtimeState is null || !runtimeState.IsAttached) && !runtimeSocketAlive)
            {
                continue;
            }

            if (runtimeSocketAlive && (runtimeState is null || !runtimeState.IsAttached))
            {
                var refreshedRuntimeState = new AgentRuntimeState(
                    BotId: profile.BotId,
                    LifecycleState: AgentLifecycleState.ActiveSession,
                    IsAttached: true,
                    LastErrorCode: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                _storage.UpsertAgentRuntimeStateAsync(refreshedRuntimeState).GetAwaiter().GetResult();
            }

            var sessionId = profile.Metadata.TryGetValue(ServerAccessSessionIdMetadataKey, out var mappedSessionId) &&
                            !string.IsNullOrWhiteSpace(mappedSessionId)
                ? mappedSessionId
                : profile.BotId;

            var serverId = profile.Metadata.TryGetValue(ServerAccessServerIdMetadataKey, out var mappedServerId)
                ? mappedServerId
                : string.Empty;
            var ownerToken = profile.Metadata.TryGetValue(ServerAccessOwnerTokenMetadataKey, out var mappedOwnerToken)
                ? mappedOwnerToken
                : string.Empty;
            var dashboardEndpoint = profile.Metadata.TryGetValue(ServerAccessDashboardEndpointMetadataKey, out var mappedDashboard)
                ? mappedDashboard
                : string.Empty;

            lock (_deployConnectionLock)
            {
                _activeDeployConnections.Add((sourceBotId, sessionId));
            }

            SetActiveServerAccess(sourceBotId, sessionId, profile.BotId, profile.Name, serverId, ownerToken, dashboardEndpoint);
        }
    }

    private void ReconcileActiveSessionsFromRuntimeSockets(BotSummaryItem sourceBot)
    {
        var sourceBotId = sourceBot.BotId;
        var sourceBotName = sourceBot.Name;
        var runtimeIdPrefix = sourceBotId + "-";
        var socketPrefix = "bbs-agent-" + runtimeIdPrefix;
        var socketSuffix = ".sock.control";

        string[] controlSockets;
        try
        {
            controlSockets = Directory.GetFiles(Path.GetTempPath(), socketPrefix + "*" + socketSuffix);
        }
        catch
        {
            return;
        }

        foreach (var controlSocketPath in controlSockets)
        {
            var fileName = Path.GetFileName(controlSocketPath);
            if (!fileName.StartsWith("bbs-agent-", StringComparison.Ordinal) ||
                !fileName.EndsWith(socketSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var runtimeBotId = fileName.Substring("bbs-agent-".Length, fileName.Length - "bbs-agent-".Length - socketSuffix.Length);
            if (!runtimeBotId.StartsWith(runtimeIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var statusReply = ReadRuntimeStatus(runtimeBotId);
            if (statusReply is null)
            {
                continue;
            }

            var activeSessionId = NormalizeActiveSessionId(statusReply.SessionId);
            if (string.IsNullOrWhiteSpace(activeSessionId))
            {
                continue;
            }

            var runtimeSuffix = runtimeBotId[runtimeIdPrefix.Length..];
            if (string.IsNullOrWhiteSpace(runtimeSuffix))
            {
                continue;
            }

            var sessionId = activeSessionId;
            var runtimeBotName = sourceBotName + "-" + runtimeSuffix;

            lock (_deployConnectionLock)
            {
                _activeDeployConnections.Add((sourceBotId, sessionId));
            }

            // Socket fallback has no guaranteed persisted owner/dashboard metadata.
            SetActiveServerAccess(
                sourceBotId,
                sessionId,
                runtimeBotId,
                runtimeBotName,
                SelectedServer?.ServerId ?? string.Empty,
                string.Empty,
                string.Empty);
        }
    }

    private AgentControlResponse? ReadRuntimeStatus(string runtimeBotId)
    {
        try
        {
            var controlSocketPath = BuildAgentControlSocketPath(runtimeBotId);
            if (!File.Exists(controlSocketPath))
            {
                return null;
            }

            var reply = SendAgentControlRequest(controlSocketPath, "status", new Dictionary<string, object>());
            if (!string.Equals(reply.Type, "status", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return reply;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeActiveSessionId(string rawSessionId)
    {
        var trimmed = rawSessionId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (int.TryParse(trimmed, out var numericSessionId) && numericSessionId <= 0)
        {
            return string.Empty;
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private void ExecuteSessionJoin(string botId, string sessionId)
    {
        var item = ActiveBotSessions.FirstOrDefault(x => x.SessionId == sessionId);
        if (item is null)
        {
            return;
        }

        if (!TryGetRuntimeSession(botId, sessionId, out var runtimeBotId, out _, out _))
        {
            BotEditorMessage = "JOIN failed: runtime session mapping not found.";
            return;
        }

        if (item.SelectedArena is null || item.SelectedArena.ArenaId <= 0)
        {
            BotEditorMessage = "Select an arena before JOIN.";
            return;
        }

        if (!int.TryParse(item.JoinHandicapPercent.Trim(), out var handicapPercent))
        {
            BotEditorMessage = "JOIN handicap must be an integer.";
            return;
        }

        try
        {
            var reply = SendAgentControlRequest(
                BuildAgentControlSocketPath(runtimeBotId),
                "join_session",
                new Dictionary<string, object>
                {
                    ["arena_id"] = item.SelectedArena.ArenaId,
                    ["handicap_percent"] = handicapPercent
                });

            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                BotEditorMessage = $"JOIN failed: {reply.Message}";
                return;
            }

            BotEditorMessage = $"JOIN requested for session {sessionId}.";
        }
        catch (Exception ex)
        {
            BotEditorMessage = $"JOIN failed: {ex.Message}";
        }
    }

    private void ExecuteSessionLeave(string botId, string sessionId)
    {
        if (!TryGetRuntimeSession(botId, sessionId, out var runtimeBotId, out _, out _))
        {
            BotEditorMessage = "LEAVE failed: runtime session mapping not found.";
            return;
        }

        try
        {
            var reply = SendAgentControlRequest(
                BuildAgentControlSocketPath(runtimeBotId),
                "leave_session",
                new Dictionary<string, object>());

            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                BotEditorMessage = $"LEAVE failed: {reply.Message}";
                return;
            }

            BotEditorMessage = $"LEAVE requested for session {sessionId}.";
        }
        catch (Exception ex)
        {
            BotEditorMessage = $"LEAVE failed: {ex.Message}";
        }
    }

    private void ExecuteSessionQuit(string botId, string sessionId)
    {
        if (!TryGetRuntimeSession(botId, sessionId, out var runtimeBotId, out _, out _))
        {
            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit: false);
            BotEditorMessage = $"Removed stale session {sessionId}.";
            return;
        }

        try
        {
            var reply = SendAgentControlRequest(
                BuildAgentControlSocketPath(runtimeBotId),
                "quit_session",
                new Dictionary<string, object>());

            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                BotEditorMessage = $"QUIT failed: {reply.Message}";
                return;
            }

            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit: false);
            BotEditorMessage = $"QUIT requested for session {sessionId}.";
            LoadBotsFromStorage();
            SelectedBot = FindBotById(botId);
        }
        catch (Exception ex)
        {
            BotEditorMessage = $"QUIT failed: {ex.Message}";
        }
    }

    private ObservableCollection<ServerArenaOptionItem> BuildArenaOptionsForServer(string serverId)
    {
        var options = new ObservableCollection<ServerArenaOptionItem>();
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return options;
        }

        if (!string.Equals(SelectedServer?.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        foreach (var arena in ServerArenaEntries)
        {
            options.Add(new ServerArenaOptionItem($"#{arena.ArenaId} - {arena.Game}", arena.ArenaId));
        }

        return options;
    }

    private void RefreshActiveSessionArenaOptions()
    {
        if (ActiveBotSessions.Count == 0)
        {
            return;
        }

        foreach (var session in ActiveBotSessions)
        {
            var selectedArenaId = session.SelectedArena?.ArenaId;
            var options = BuildArenaOptionsForServer(session.ServerId);

            session.ArenaOptions.Clear();
            foreach (var option in options)
            {
                session.ArenaOptions.Add(option);
            }

            if (selectedArenaId is > 0)
            {
                session.SelectedArena = session.ArenaOptions.FirstOrDefault(x => x.ArenaId == selectedArenaId);
            }
            else if (session.ArenaOptions.Count == 1)
            {
                session.SelectedArena = session.ArenaOptions[0];
            }
            else if (session.SelectedArena is not null && session.ArenaOptions.Count == 0)
            {
                session.SelectedArena = null;
            }
        }
    }

    private string ResolveServerName(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return "Unknown server";
        }

        var server = FindServerById(serverId);
        return server is null ? serverId : server.Name;
    }

    private void PruneStaleActiveSessionCaches()
    {
        List<(string BotId, string SessionId)> stale;

        lock (_activeAccessCacheLock)
        {
            stale = _activeSessionsByBotAndSession
                .Where(s => !File.Exists(BuildAgentControlSocketPath(s.Value.RuntimeBotId)))
                .Select(s => s.Key)
                .ToList();
        }

        foreach (var session in stale)
        {
            lock (_deployConnectionLock)
            {
                _activeDeployConnections.Remove((session.BotId, session.SessionId));
            }

            ClearActiveServerAccess(session.BotId, session.SessionId);
        }
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

    private static string ResolveServerDashboardEndpoint(ServerSummaryItem server)
    {
        var value = FirstNonEmptyMetadataValue(server.Metadata, DashboardEndpointMetadataKeys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var dashboardPort = ParseDashboardPort(server.Metadata) ?? DashboardPortFallback;
        var scheme = server.UseTls ? "https" : "http";
        return BuildBaseEndpoint(scheme, server.Host, dashboardPort);
    }

    private void HandleOrchestrationException(string action, string botId, string errorCode, Exception ex)
    {
        if (string.Equals(action, "deploy", StringComparison.OrdinalIgnoreCase) &&
            !errorCode.EndsWith("_mvfix", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = errorCode + "_mvfix";
        }

        BotEditorMessage = $"Unable to {action} bot ({errorCode}). You can retry without restarting the app.";
        var topStackFrame = ex.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        var fields = new Dictionary<string, string>
        {
            ["action"] = action,
            ["bot_id"] = botId,
            ["error_code"] = errorCode,
            ["exception_type"] = ex.GetType().Name,
            ["exception_message"] = ex.Message,
            ["exception_top_frame"] = topStackFrame
        };

        _logger.Log(LogLevel.Warning, "bot_orchestration_exception", "Bot orchestration command failed.", fields);
        _logger.Log(LogLevel.Warning, "bot_orchestration_exception_mvfix", "Bot orchestration command failed (mvfix path).", fields);

        LoadBotsFromStorage();
        SelectedBot = FindBotById(botId);
        TriggerServerAccessRefresh();
    }

    private void LoadBotsFromStorage()
    {
        var profiles = _storage.ListBotProfilesAsync().GetAwaiter().GetResult();

        PruneStaleActiveSessionCaches();

        Bots.Clear();
        foreach (var profile in profiles)
        {
            if (IsRuntimeInstanceProfile(profile))
            {
                continue;
            }

            var runtimeState = _storage.GetAgentRuntimeStateAsync(profile.BotId).GetAwaiter().GetResult();
            if (runtimeState is not null &&
                runtimeState.LifecycleState == AgentLifecycleState.ActiveSession &&
                !HasActiveDeployConnection(profile.BotId))
            {
                runtimeState = new AgentRuntimeState(
                    BotId: runtimeState.BotId,
                    LifecycleState: AgentLifecycleState.Idle,
                    IsAttached: false,
                    LastErrorCode: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                _storage.UpsertAgentRuntimeStateAsync(runtimeState).GetAwaiter().GetResult();
            }

            Bots.Add(BotSummaryItem.FromProfile(
                profile,
                runtimeState,
                DeploySelectedBotCommand));
        }

        OnPropertyChanged(nameof(Bots));
        RefreshActiveBotSessionsProjection();
    }

    private static bool IsRuntimeInstanceProfile(BotProfile profile)
    {
        if (profile.Metadata.TryGetValue("runtime_instance", out var runtimeFlag) &&
            string.Equals(runtimeFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void LoadServersFromStorage()
    {
        var servers = _storage.ListKnownServersAsync().GetAwaiter().GetResult();

        Servers.Clear();
        foreach (var server in servers)
        {
            var cache = _storage.GetServerPluginCacheAsync(server.ServerId).GetAwaiter().GetResult();
            Servers.Add(ServerSummaryItem.FromKnownServer(server, cache));
        }

        OnPropertyChanged(nameof(Servers));
        ((RelayCommand)ReprobeServersCommand).RaiseCanExecuteChanged();
        RefreshSelectedServerDetail();
        RefreshActiveBotSessionsProjection();
    }

    private BotSummaryItem? FindBotById(string botId)
    {
        foreach (var bot in Bots)
        {
            if (bot.BotId == botId)
            {
                return bot;
            }
        }

        return null;
    }

    private ServerSummaryItem? FindServerById(string serverId)
    {
        foreach (var server in Servers)
        {
            if (server.ServerId == serverId)
            {
                return server;
            }
        }

        return null;
    }

    private void PopulateBotEditor(BotSummaryItem bot)
    {
        BotEditorName = bot.Name;
        BotEditorLaunchPath = bot.LaunchPath;
        BotEditorArgs = string.Join(" ", bot.LaunchArgs);
        BotEditorMetadata = MainWindowViewModelHelpers.FormatMetadata(bot.Metadata);
        BotEditorMessage = $"Editing bot profile: {bot.Name}";
    }

    private void PopulateServerEditor(ServerSummaryItem server)
    {
        ServerEditorName = server.Name;
        ServerEditorHost = server.Host;
        ServerEditorPort = server.Port.ToString();
        ServerEditorUseTls = server.UseTls;
        ServerEditorMetadata = MainWindowViewModelHelpers.FormatMetadata(server.Metadata);
        ServerEditorMessage = $"Editing known server: {server.Name}";
    }

    private void RefreshSelectedServerDetail()
    {
        IsServerDetailLoading = true;
        ServerMetadataEntries.Clear();
        ServerPluginCatalogEntries.Clear();

        var server = SelectedServer;
        if (server is null)
        {
            ServerCatalogStatus = "Select a server to view cached plugin catalog.";
            IsServerDetailLoading = false;
            OnPropertyChanged(nameof(HasServerMetadata));
            OnPropertyChanged(nameof(HasServerPluginCatalog));
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
            OnPropertyChanged(nameof(ServerDetailEndpoint));
            OnPropertyChanged(nameof(ServerDetailProbeStatus));
            return;
        }

        foreach (var metadata in server.Metadata)
        {
            ServerMetadataEntries.Add(new ServerMetadataEntryItem(metadata.Key, metadata.Value));
        }

        foreach (var plugin in server.CachedPlugins)
        {
            ServerPluginCatalogEntries.Add(new ServerPluginCatalogItem(plugin.Name, plugin.DisplayName, plugin.Version, plugin.Metadata));
        }

        EnsureOwnerArenaPluginSelection();

        ServerCatalogStatus = server.CachedPlugins.Count == 0
            ? "No cached plugins available."
            : $"Cached plugins: {server.CachedPlugins.Count}";

        IsServerDetailLoading = false;
        OnPropertyChanged(nameof(HasServerMetadata));
        OnPropertyChanged(nameof(HasServerPluginCatalog));
        OnPropertyChanged(nameof(ShowServerMetadataEmpty));
        OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
        OnPropertyChanged(nameof(ServerDetailEndpoint));
        OnPropertyChanged(nameof(ServerDetailProbeStatus));

        TriggerSelectedServerCatalogRefresh(server);
        _ = RefreshSelectedServerArenasAsync();
    }

    private void EnsureOwnerArenaPluginSelection()
    {
        if (ServerPluginCatalogEntries.Count == 0)
        {
            OwnerArenaSelectedPlugin = string.Empty;
            OwnerArenaArgs = string.Empty;
            return;
        }

        var exists = ServerPluginCatalogEntries.Any(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            OwnerArenaSelectedPlugin = ServerPluginCatalogEntries[0].Name;
            return;
        }

        SyncOwnerArenaArgsFromSelectedPlugin();
    }

    private void SyncOwnerArenaArgsFromSelectedPlugin()
    {
        if (string.IsNullOrWhiteSpace(OwnerArenaSelectedPlugin))
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        var selectedPlugin = ServerPluginCatalogEntries
            .FirstOrDefault(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        if (selectedPlugin is null)
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        if (!selectedPlugin.Metadata.TryGetValue("args_json", out var argsJson) || string.IsNullOrWhiteSpace(argsJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var parts = new List<string>();
            foreach (var arg in doc.RootElement.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = arg.TryGetProperty("key", out var keyElement)
                    ? keyElement.GetString()
                    : null;
                var defaultValue = arg.TryGetProperty("default_value", out var defaultElement)
                    ? defaultElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(defaultValue))
                {
                    continue;
                }

                parts.Add($"{key.Trim()}={defaultValue.Trim()}");
            }

            if (parts.Count > 0)
            {
                OwnerArenaArgs = string.Join(' ', parts);
            }
        }
        catch (JsonException)
        {
        }
    }

    private void SaveServerProfile()
    {
        if (!int.TryParse(ServerEditorPort.Trim(), out var parsedPort))
        {
            ServerEditorMessage = "Cannot save server: server_port_invalid";
            return;
        }

        var serverId = SelectedServer?.ServerId ?? Guid.NewGuid().ToString("N");
        var createdAt = SelectedServer?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.UtcNow;

        var server = KnownServer.Create(
            serverId: serverId,
            name: ServerEditorName.Trim(),
            host: ServerEditorHost.Trim(),
            port: parsedPort,
            useTls: ServerEditorUseTls,
            metadata: MainWindowViewModelHelpers.ParseMetadata(ServerEditorMetadata),
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var serverErrors = server.Validate();
        if (serverErrors.Count > 0)
        {
            ServerEditorMessage = $"Cannot save server: {string.Join(", ", serverErrors)}";
            return;
        }

        _storage.UpsertKnownServerAsync(server).GetAwaiter().GetResult();
        LoadServersFromStorage();
        SelectedServer = FindServerById(serverId);
        TriggerServerAccessRefresh();
        var dashboardPortHint = server.Port == BotTcpDefaultPort
            ? " Saved, but 8080 is commonly the bot TCP port; dashboard is often 3000."
            : string.Empty;
        ServerEditorMessage = $"Saved known server: {server.Name}.{dashboardPortHint}";
        _logger.Log(LogLevel.Information, "known_server_saved", "Known server and plugin cache persisted.",
            new Dictionary<string, string>
            {
                ["server_id"] = server.ServerId,
                ["name"] = server.Name,
                ["dashboard_port_hint"] = dashboardPortHint.Length == 0 ? "none" : "bot_port_likely"
            });
    }

    private void TriggerServerAccessRefresh()
    {
        _ = RefreshServerAccessMetadataAsync();
    }

    private async Task RefreshServerAccessMetadataAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _serverAccessRefreshVersion);
        IsServerAccessLoading = true;
        ServerAccessStatus = "Refreshing server access metadata...";

        var selectedServerId = SelectedServer?.ServerId;
        var selectedBotId = SelectedBot?.BotId;

        try
        {
            var resolved = await Task.Run(() => ResolveServerAccessMetadata(selectedServerId, selectedBotId));
            if (refreshVersion != Volatile.Read(ref _serverAccessRefreshVersion))
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

            RefreshOwnerTokenActionProjection();
        }
        catch
        {
            if (refreshVersion != Volatile.Read(ref _serverAccessRefreshVersion))
            {
                return;
            }

            ServerAccessMetadata = ServerAccessMetadata.Invalid("Failed to refresh server access metadata.");
            ServerAccessOwnerToken = "-";
            ServerAccessDashboardEndpoint = "-";
            ServerAccessStatus = "Failed to refresh server access metadata.";
            OwnerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";
            RefreshOwnerTokenActionProjection();
        }
        finally
        {
            if (refreshVersion == Volatile.Read(ref _serverAccessRefreshVersion))
            {
                IsServerAccessLoading = false;
            }
        }
    }

    private void RefreshOwnerTokenActionProjection()
    {
        OnPropertyChanged(nameof(HasValidServerAccess));
        OnPropertyChanged(nameof(ShowOwnerTokenActions));
        OnPropertyChanged(nameof(ShowOwnerTokenActionsUnavailable));
        ((RelayCommand)CreateArenaCommand).RaiseCanExecuteChanged();
        ((RelayCommand)JoinArenaCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
    }

    private ServerAccessMetadata ResolveServerAccessMetadata(string? selectedServerId, string? selectedBotId)
    {
        // Server metadata is the canonical owner-token source in Server context.
        if (!string.IsNullOrWhiteSpace(selectedServerId))
        {
            var knownServerAccess = ResolveKnownServerAccessMetadata(selectedServerId);
            if (knownServerAccess.IsValid || _currentContext == WorkspaceContext.ServerDetails)
            {
                return knownServerAccess;
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedBotId) &&
            TryGetActiveServerAccess(selectedBotId, selectedServerId, out var activeServerId, out var activeAccess) &&
            (string.IsNullOrWhiteSpace(selectedServerId) || string.Equals(selectedServerId, activeServerId, StringComparison.OrdinalIgnoreCase)))
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

        return ServerAccessMetadataResolver.Resolve(profile, runtimeState, selectedServerId);
    }

    private ServerAccessMetadata ResolveKnownServerAccessMetadata(string? selectedServerId)
    {
        var server = !string.IsNullOrWhiteSpace(selectedServerId)
            ? FindServerById(selectedServerId)
            : SelectedServer;

        if (server is null)
        {
            return ServerAccessMetadata.Invalid("No server selected for access metadata.");
        }

        var ownerToken = FirstNonEmptyMetadataValue(server.Metadata, new[]
        {
            ClientOwnerTokenMetadataKey,
            ServerAccessOwnerTokenMetadataKey,
            "owner_token",
            "server.owner_token"
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

    private void SetActiveServerAccess(string botId, string sessionId, string runtimeBotId, string runtimeBotName, string serverId, string ownerToken, string dashboardEndpoint)
    {
        var access = string.IsNullOrWhiteSpace(ownerToken)
            ? ServerAccessMetadata.Invalid("Owner token is not available for this runtime session yet.")
            : new ServerAccessMetadata(
                IsValid: true,
                OwnerToken: ownerToken,
                DashboardEndpoint: dashboardEndpoint,
                StatusMessage: "Server access metadata loaded from active runtime session.",
                Source: "active-session-cache");

        lock (_activeAccessCacheLock)
        {
            _activeSessionsByBotAndSession[(botId, sessionId)] = (runtimeBotId, runtimeBotName, serverId, access);
        }
    }

    private void ClearActiveServerAccess(string botId, string sessionId)
    {
        lock (_activeAccessCacheLock)
        {
            _activeSessionsByBotAndSession.Remove((botId, sessionId));
        }
    }

    private bool TryGetActiveServerAccess(string botId, string? targetServerId, out string serverId, out ServerAccessMetadata access)
    {
        lock (_activeAccessCacheLock)
        {
            // If a specific target server is requested, find the session for that server
            if (!string.IsNullOrWhiteSpace(targetServerId))
            {
                foreach (var kvp in _activeSessionsByBotAndSession)
                {
                    if (kvp.Key.BotId == botId && kvp.Value.ServerId == targetServerId)
                    {
                        serverId = kvp.Value.ServerId;
                        access = kvp.Value.Access;
                        return true;
                    }
                }
            }
            else
            {
                // If no specific server requested, return the first active session for this bot
                foreach (var kvp in _activeSessionsByBotAndSession)
                {
                    if (kvp.Key.BotId == botId)
                    {
                        serverId = kvp.Value.ServerId;
                        access = kvp.Value.Access;
                        return true;
                    }
                }
            }
        }

        serverId = string.Empty;
        access = ServerAccessMetadata.Invalid("No active session metadata cached.");
        return false;
    }

    private bool TryGetRuntimeSession(string sourceBotId, string sessionId, out string runtimeBotId, out string runtimeBotName, out string serverId)
    {
        lock (_activeAccessCacheLock)
        {
            if (_activeSessionsByBotAndSession.TryGetValue((sourceBotId, sessionId), out var session))
            {
                runtimeBotId = session.RuntimeBotId;
                runtimeBotName = session.RuntimeBotName;
                serverId = session.ServerId;
                return true;
            }
        }

        runtimeBotId = string.Empty;
        runtimeBotName = string.Empty;
        serverId = string.Empty;
        return false;
    }
}
