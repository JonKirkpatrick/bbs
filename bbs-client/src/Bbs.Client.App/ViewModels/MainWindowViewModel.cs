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
using Avalonia.Media;
using Avalonia.Threading;
using System.Windows.Input;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int ServerProbeTimeoutMs = 1200;
    private const int BotWelcomeReadTimeoutMs = 500;
    private const int ServerCatalogFetchTimeoutMs = 2000;
    private const int ServerCatalogSelectionRefreshCooldownMs = 5000;
    private const int DashboardPortFallback = 3000;
    private const int BotTcpDefaultPort = 8080;
    private const int ServerProbeMaxAttempts = 2;
    private const int ServerProbeRetryDelayMs = 200;
    private const int DeployHandshakeTimeoutMs = 3000;
    private const string ProbeStatusMetadataKey = "probe_status";
    private const string ProbeLastCheckedMetadataKey = "probe_last_checked_utc";
    private const string ProbeLastErrorMetadataKey = "probe_last_error";
    private const string ServerAccessServerIdMetadataKey = "server_access.server_id";
    private const string ServerAccessSessionIdMetadataKey = "server_access.session_id";
    private const string ServerAccessOwnerTokenMetadataKey = "server_access.owner_token";
    private const string ServerAccessDashboardEndpointMetadataKey = "server_access.dashboard_endpoint";
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
    private readonly IClientStorage _storage;
    private readonly IBotOrchestrationService _orchestration;
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
    private string _serverAccessStatus = "Select an armed bot session to load server access metadata.";
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
    private readonly HashSet<string> _activeDeployConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activeAccessCacheLock = new();
    private readonly Dictionary<string, (string ServerId, ServerAccessMetadata Access)> _activeServerAccessByBotId = new(StringComparer.OrdinalIgnoreCase);

    public MainWindowViewModel(IClientLogger logger, IClientStorage storage, IBotOrchestrationService orchestration)
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

        EmitSampleLogCommand = new RelayCommand(EmitSampleLog);
        ToggleLeftPanelCommand = new RelayCommand(ToggleLeftPanel);
        ToggleRightPanelCommand = new RelayCommand(ToggleRightPanel);
        SetHomeContextCommand = new RelayCommand(SetHomeContext);
        SetBotContextCommand = new RelayCommand(SetBotContextFromSelection, () => SelectedBot is not null);
        SetServerContextCommand = new RelayCommand(SetServerContextFromSelection, () => SelectedServer is not null);
        SaveBotProfileCommand = new RelayCommand(SaveBotProfile);
        StartNewBotCommand = new RelayCommand(StartNewBot);
        ArmSelectedBotCommand = new RelayCommand(ArmSelectedBot, () => SelectedBot is not null);
        DisarmSelectedBotCommand = new RelayCommand(DisarmSelectedBot, () => SelectedBot is not null);
        DeploySelectedBotCommand = new RelayCommand(DeploySelectedBotToSelectedServer, CanDeploySelectedBot);
        SaveServerProfileCommand = new RelayCommand(SaveServerProfile);
        StartNewServerCommand = new RelayCommand(StartNewServer);
        ReprobeServersCommand = new RelayCommand(ReprobeServers, () => !IsServerProbeInProgress && Servers.Count > 0);
        RefreshServerAccessCommand = new RelayCommand(RefreshServerAccessMetadata);
        CreateArenaStubCommand = new RelayCommand(ExecuteCreateArenaStub, CanExecuteOwnerTokenActionStubs);
        JoinArenaStubCommand = new RelayCommand(ExecuteJoinArenaStub, CanExecuteOwnerTokenActionStubs);

        _currentContext = WorkspaceContext.Home;
        LoadBotsFromStorage();
        LoadServersFromStorage();
        _selectedBot = Bots.Count > 0 ? Bots[0] : null;
        _selectedServer = Servers.Count > 0 ? Servers[0] : null;
        if (_selectedBot is not null)
        {
            PopulateBotEditor(_selectedBot);
        }

        if (_selectedServer is not null)
        {
            PopulateServerEditor(_selectedServer);
        }

        RefreshSelectedServerDetail();
        TriggerServerAccessRefresh();

        RefreshContextProjection();
        StartStartupServerProbe();
    }

    public string WindowTitle => "BBS Client Alpha";
    public string WorkspaceTitle { get; private set; } = "Context Host Ready";
    public string WorkspaceDescription { get; private set; } = "Select a bot or server to load activity in this center workspace.";

    public string CurrentContextLabel => $"Context: {_currentContext}";
    public bool ShowBotEditor => _currentContext != WorkspaceContext.ServerDetails;
    public bool ShowServerEditor => _currentContext == WorkspaceContext.ServerDetails;
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
            ((RelayCommand)CreateArenaStubCommand).RaiseCanExecuteChanged();
            ((RelayCommand)JoinArenaStubCommand).RaiseCanExecuteChanged();
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
            OnPropertyChanged(nameof(LeftPanelToggleText));
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
            OnPropertyChanged(nameof(RightPanelToggleText));
        }
    }

    public bool IsLeftPanelCollapsed => !IsLeftPanelExpanded;
    public bool IsRightPanelCollapsed => !IsRightPanelExpanded;

    public GridLength LeftPanelWidth => IsLeftPanelExpanded ? new GridLength(280) : new GridLength(56);
    public GridLength RightPanelWidth => IsRightPanelExpanded ? new GridLength(280) : new GridLength(56);
    public string LeftPanelToggleText => IsLeftPanelExpanded ? "Collapse Bots" : "Expand Bots";
    public string RightPanelToggleText => IsRightPanelExpanded ? "Collapse Servers" : "Expand Servers";

    public ObservableCollection<BotSummaryItem> Bots { get; }
    public ObservableCollection<ServerSummaryItem> Servers { get; }
    public ObservableCollection<ServerMetadataEntryItem> ServerMetadataEntries { get; }
    public ObservableCollection<ServerPluginCatalogItem> ServerPluginCatalogEntries { get; }

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
            ((RelayCommand)ArmSelectedBotCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DisarmSelectedBotCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                PopulateBotEditor(value);
                if (_currentContext != WorkspaceContext.ServerDetails)
                {
                    _currentContext = WorkspaceContext.BotDetails;
                    RefreshContextProjection();
                }
            }

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
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                _currentContext = WorkspaceContext.ServerDetails;
                PopulateServerEditor(value);
                RefreshContextProjection();
            }

            TriggerServerAccessRefresh();
        }
    }

    public ICommand EmitSampleLogCommand { get; }
    public ICommand ToggleLeftPanelCommand { get; }
    public ICommand ToggleRightPanelCommand { get; }
    public ICommand SetHomeContextCommand { get; }
    public ICommand SetBotContextCommand { get; }
    public ICommand SetServerContextCommand { get; }
    public ICommand SaveBotProfileCommand { get; }
    public ICommand StartNewBotCommand { get; }
    public ICommand ArmSelectedBotCommand { get; }
    public ICommand DisarmSelectedBotCommand { get; }
    public ICommand DeploySelectedBotCommand { get; }
    public ICommand SaveServerProfileCommand { get; }
    public ICommand StartNewServerCommand { get; }
    public ICommand ReprobeServersCommand { get; }
    public ICommand RefreshServerAccessCommand { get; }
    public ICommand CreateArenaStubCommand { get; }
    public ICommand JoinArenaStubCommand { get; }

    private void EmitSampleLog()
    {
        _logger.Log(LogLevel.Information, "workspace_event", "Unified workspace shell action invoked.",
            new Dictionary<string, string>
            {
                ["source"] = "main_window",
                ["feature"] = "unified_workspace_shell"
            });
    }

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

    private bool CanExecuteOwnerTokenActionStubs()
    {
        return HasValidServerAccess && !IsServerAccessLoading && SelectedServer is not null;
    }

    private bool CanDeploySelectedBot()
    {
        return SelectedBot is { IsArmed: true } &&
               SelectedBot.LifecycleState != AgentLifecycleState.ActiveSession &&
               SelectedServer is not null &&
               _currentContext == WorkspaceContext.ServerDetails &&
               !HasActiveDeployConnection(SelectedBot.BotId) &&
               !IsServerAccessLoading;
    }

    private void ExecuteCreateArenaStub()
    {
        _ = ExecuteOwnerTokenActionAsync(OwnerTokenActionType.CreateArena);
    }

    private void ExecuteJoinArenaStub()
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
            _logger.Log(LogLevel.Warning, "owner_token_action_blocked", "Owner-token action stub blocked by precondition check.",
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
        _currentContext = WorkspaceContext.Home;
        RefreshContextProjection();
    }

    private void SetBotContextFromSelection()
    {
        _currentContext = WorkspaceContext.BotDetails;
        RefreshContextProjection();
    }

    private void SetServerContextFromSelection()
    {
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
            default:
                WorkspaceTitle = "Context Host Ready";
                WorkspaceDescription = "Select a bot or server to load activity in this center workspace.";
                break;
        }

        OnPropertyChanged(nameof(CurrentContextLabel));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
        OnPropertyChanged(nameof(ShowBotEditor));
        OnPropertyChanged(nameof(ShowServerEditor));
        ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
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
            launchArgs: ParseArgs(BotEditorArgs),
            metadata: ParseMetadata(BotEditorMetadata),
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

    private void ArmSelectedBot()
    {
        if (SelectedBot is null)
        {
            return;
        }

        var selectedBotId = SelectedBot.BotId;
        var profile = SelectedBot.ToProfile();
        try
        {
            var result = _orchestration.ArmBotAsync(profile).GetAwaiter().GetResult();
            LoadBotsFromStorage();
            SelectedBot = FindBotById(profile.BotId);
            TriggerServerAccessRefresh();
            BotEditorMessage = result.Message;
        }
        catch (SocketException socketException)
        {
            HandleOrchestrationException("arm", selectedBotId, $"socket_{socketException.SocketErrorCode}".ToLowerInvariant(), socketException);
        }
        catch (InvalidOperationException invalidOperationException)
        {
            HandleOrchestrationException("arm", selectedBotId, "stale_process_handle", invalidOperationException);
        }
        catch (Exception ex)
        {
            HandleOrchestrationException("arm", selectedBotId, "orchestration_failure", ex);
        }
    }

    private void DisarmSelectedBot()
    {
        if (SelectedBot is null)
        {
            return;
        }

        var selectedBotId = SelectedBot.BotId;
        var profile = SelectedBot.ToProfile();
        try
        {
            DisconnectActiveDeploymentConnection(selectedBotId, sendQuit: true);
            ClearActiveServerAccess(selectedBotId);
            var result = _orchestration.DisarmBotAsync(profile).GetAwaiter().GetResult();
            LoadBotsFromStorage();
            SelectedBot = FindBotById(profile.BotId);
            TriggerServerAccessRefresh();
            BotEditorMessage = result.Message;
        }
        catch (SocketException socketException)
        {
            HandleOrchestrationException("disarm", selectedBotId, $"socket_{socketException.SocketErrorCode}".ToLowerInvariant(), socketException);
        }
        catch (InvalidOperationException invalidOperationException)
        {
            HandleOrchestrationException("disarm", selectedBotId, "stale_process_handle", invalidOperationException);
        }
        catch (Exception ex)
        {
            HandleOrchestrationException("disarm", selectedBotId, "orchestration_failure", ex);
        }
    }

    private void DeploySelectedBotToSelectedServer()
    {
        var bot = SelectedBot;
        var server = SelectedServer;
        if (bot is null)
        {
            BotEditorMessage = "Select a bot before deploy.";
            return;
        }

        if (!bot.IsArmed)
        {
            BotEditorMessage = "Deploy requires an armed bot.";
            return;
        }

        if (server is null)
        {
            BotEditorMessage = "Deploy requires a selected server.";
            return;
        }

        if (_currentContext != WorkspaceContext.ServerDetails)
        {
            BotEditorMessage = "Deploy requires an active server in the center activity space.";
            return;
        }

        if (bot.LifecycleState == AgentLifecycleState.ActiveSession || HasActiveDeployConnection(bot.BotId))
        {
            BotEditorMessage = "This bot is already deployed. Disconnect it before deploying again.";
            return;
        }

        try
        {
            var sourceProfile = bot.ToProfile();
            var serverGlobalId = ResolveServerGlobalId(server);
            var previousCredential = _storage.GetBotServerCredentialAsync(sourceProfile.BotId, server.ServerId, serverGlobalId).GetAwaiter().GetResult();
            var registerResponse = RegisterBotSessionViaAgentControl(server, sourceProfile);
            var attachedMetadata = new Dictionary<string, string>(sourceProfile.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [ServerAccessServerIdMetadataKey] = server.ServerId
            };

            if (!string.IsNullOrWhiteSpace(registerResponse.DashboardEndpoint))
            {
                attachedMetadata[ServerAccessDashboardEndpointMetadataKey] = registerResponse.DashboardEndpoint;
            }
            var credential = BotServerCredential.Create(
                clientBotId: sourceProfile.BotId,
                serverId: server.ServerId,
                serverGlobalId: serverGlobalId,
                serverBotId: registerResponse.ServerBotId,
                serverBotSecret: registerResponse.ServerBotSecret);
            _storage.UpsertBotServerCredentialAsync(credential).GetAwaiter().GetResult();

            var attachedProfile = BotProfile.Create(
                botId: sourceProfile.BotId,
                name: sourceProfile.Name,
                launchPath: sourceProfile.LaunchPath,
                avatarImagePath: sourceProfile.AvatarImagePath,
                launchArgs: sourceProfile.LaunchArgs,
                metadata: attachedMetadata,
                createdAtUtc: sourceProfile.CreatedAtUtc,
                updatedAtUtc: DateTimeOffset.UtcNow);

            var activeSessionState = new AgentRuntimeState(
                BotId: sourceProfile.BotId,
                LifecycleState: AgentLifecycleState.ActiveSession,
                IsArmed: true,
                LastErrorCode: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow);

            _storage.UpsertBotProfileAsync(attachedProfile).GetAwaiter().GetResult();
            _storage.UpsertAgentRuntimeStateAsync(activeSessionState).GetAwaiter().GetResult();

            lock (_deployConnectionLock)
            {
                if (!_activeDeployConnections.Contains(sourceProfile.BotId))
                {
                    _activeDeployConnections.Add(sourceProfile.BotId);
                }
            }

            SetActiveServerAccess(sourceProfile.BotId, server.ServerId, registerResponse.OwnerToken, registerResponse.DashboardEndpoint);

            LoadBotsFromStorage();
            SelectedBot = FindBotById(sourceProfile.BotId);
            TriggerServerAccessRefresh();
            BotEditorMessage = $"Deployed {sourceProfile.Name} to {server.Name}; active session established.";

            _logger.Log(LogLevel.Information, "bot_deploy_attached", "Bot deploy completed server register handshake and attached active session metadata.",
                new Dictionary<string, string>
                {
                    ["bot_id"] = sourceProfile.BotId,
                    ["server_id"] = server.ServerId,
                    ["session_id"] = registerResponse.SessionId,
                    ["dashboard_endpoint"] = registerResponse.DashboardEndpoint
                });

            if (previousCredential is { } existingCredential &&
                (!string.Equals(existingCredential.ServerBotId, registerResponse.ServerBotId, StringComparison.Ordinal) ||
                 !string.Equals(existingCredential.ServerBotSecret, registerResponse.ServerBotSecret, StringComparison.Ordinal)))
            {
                _logger.Log(LogLevel.Information, "credentials_refreshed_after_auth_reset", "Server credentials were refreshed during deploy after authentication mismatch.",
                    new Dictionary<string, string>
                    {
                        ["bot_id"] = sourceProfile.BotId,
                        ["server_id"] = server.ServerId,
                        ["previous_server_bot_id"] = existingCredential.ServerBotId ?? string.Empty,
                        ["new_server_bot_id"] = registerResponse.ServerBotId ?? string.Empty,
                        ["server_global_id"] = serverGlobalId ?? string.Empty,
                        ["secret_rotated"] = (!string.Equals(existingCredential.ServerBotSecret, registerResponse.ServerBotSecret, StringComparison.Ordinal)).ToString()
                    });
            }
        }
        catch (Exception ex)
        {
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
        var serverBotId = accessReply.BotId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(serverBotId))
        {
            throw new InvalidOperationException("Agent did not return a valid session_id/bot_id after server_connect.");
        }

        return new RegisterHandshakeResult(
            SessionId: sessionId,
            ServerBotId: serverBotId,
            ServerBotSecret: string.IsNullOrWhiteSpace(connectReply.BotSecret)
                ? accessReply.BotSecret
                : connectReply.BotSecret,
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
        var botSecret = payload.TryGetProperty("bot_secret", out var botSecretNode) ? botSecretNode.ToString() : string.Empty;
        var ownerToken = payload.TryGetProperty("owner_token", out var ownerTokenNode) ? ownerTokenNode.ToString() : string.Empty;
        var dashboardEndpoint = payload.TryGetProperty("dashboard_endpoint", out var endpointNode) ? endpointNode.ToString() : string.Empty;
        var dashboardHost = payload.TryGetProperty("dashboard_host", out var hostNode) ? hostNode.ToString() : string.Empty;
        var dashboardPort = payload.TryGetProperty("dashboard_port", out var portNode) ? portNode.ToString() : string.Empty;

        return new AgentControlResponse(type, id, message, sessionId, botId, botSecret, ownerToken, dashboardEndpoint, dashboardHost, dashboardPort);
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

    private static string? ResolveServerGlobalId(ServerSummaryItem server)
    {
        var raw = FirstNonEmptyMetadataValue(server.Metadata, ServerGlobalIdMetadataKeys);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
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
            return _activeDeployConnections.Contains(botId);
        }
    }

    private void DisconnectActiveDeploymentConnection(string botId, bool sendQuit)
    {
        lock (_deployConnectionLock)
        {
            _activeDeployConnections.Remove(botId);
        }
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
        BotEditorMessage = $"Unable to {action} bot ({errorCode}). You can retry without restarting the app.";
        _logger.Log(LogLevel.Warning, "bot_orchestration_exception", "Bot orchestration command failed.",
            new Dictionary<string, string>
            {
                ["action"] = action,
                ["bot_id"] = botId,
                ["error_code"] = errorCode,
                ["exception_type"] = ex.GetType().Name
            });

        LoadBotsFromStorage();
        SelectedBot = FindBotById(botId);
        TriggerServerAccessRefresh();
    }

    private void LoadBotsFromStorage()
    {
        var profiles = _storage.ListBotProfilesAsync().GetAwaiter().GetResult();

        Bots.Clear();
        foreach (var profile in profiles)
        {
            var runtimeState = _storage.GetAgentRuntimeStateAsync(profile.BotId).GetAwaiter().GetResult();
            if (runtimeState is not null &&
                runtimeState.LifecycleState == AgentLifecycleState.ActiveSession &&
                !HasActiveDeployConnection(profile.BotId))
            {
                runtimeState = new AgentRuntimeState(
                    BotId: runtimeState.BotId,
                    LifecycleState: AgentLifecycleState.Idle,
                    IsArmed: true,
                    LastErrorCode: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                _storage.UpsertAgentRuntimeStateAsync(runtimeState).GetAwaiter().GetResult();
            }

            Bots.Add(BotSummaryItem.FromProfile(
                profile,
                runtimeState,
                ArmSelectedBotCommand,
                DisarmSelectedBotCommand,
                DeploySelectedBotCommand));
        }

        OnPropertyChanged(nameof(Bots));
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
        BotEditorMetadata = FormatMetadata(bot.Metadata);
        BotEditorMessage = $"Editing bot profile: {bot.Name}";
    }

    private void PopulateServerEditor(ServerSummaryItem server)
    {
        ServerEditorName = server.Name;
        ServerEditorHost = server.Host;
        ServerEditorPort = server.Port.ToString();
        ServerEditorUseTls = server.UseTls;
        ServerEditorMetadata = FormatMetadata(server.Metadata);
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
            metadata: ParseMetadata(ServerEditorMetadata),
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

    private void StartStartupServerProbe()
    {
        _ = RunServerProbeCycleAsync(trigger: "startup", updateEditorStatus: false);
    }

    private async Task RunServerProbeCycleAsync(string trigger, bool updateEditorStatus)
    {
        if (!TryBeginProbeCycle())
        {
            if (updateEditorStatus)
            {
                ServerEditorMessage = "Probe already in progress.";
            }

            return;
        }

        if (updateEditorStatus)
        {
            ServerEditorMessage = "Probing known servers...";
        }

        try
        {
            var result = await ProbeKnownServersAsync(CancellationToken.None);
            if (updateEditorStatus)
            {
                ServerEditorMessage = $"Probe complete: {result.ReachableCount} reachable, {result.UnreachableCount} unreachable.";
            }

            _logger.Log(LogLevel.Information, "server_probe_cycle_completed", "Known server probe cycle completed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["reachable"] = result.ReachableCount.ToString(),
                    ["unreachable"] = result.UnreachableCount.ToString()
                });
        }
        catch (Exception ex)
        {
            if (updateEditorStatus)
            {
                ServerEditorMessage = "Probe failed. See logs for details.";
            }

            _logger.Log(LogLevel.Warning, "server_probe_cycle_failed", "Known server probe cycle failed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["error"] = ex.GetType().Name
                });
        }
        finally
        {
            EndProbeCycle();
        }
    }

    private async Task<(int ReachableCount, int UnreachableCount)> ProbeKnownServersAsync(CancellationToken cancellationToken)
    {
        var knownServers = await _storage.ListKnownServersAsync(cancellationToken);
        if (knownServers.Count == 0)
        {
            return (0, 0);
        }

        var reachableCount = 0;
        var unreachableCount = 0;

        foreach (var knownServer in knownServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probeResult = await ProbeKnownServerWithRetryAsync(knownServer, cancellationToken);
            var metadata = new Dictionary<string, string>(knownServer.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [ProbeStatusMetadataKey] = probeResult.IsReachable ? "reachable" : "unreachable",
                [ProbeLastCheckedMetadataKey] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (probeResult.IsReachable)
            {
                metadata.Remove(ProbeLastErrorMetadataKey);
                reachableCount++;

                var pluginCatalogResult = await RefreshServerPluginCatalogCacheAsync(knownServer, cancellationToken);
                if (pluginCatalogResult.Updated)
                {
                    metadata["probe_plugin_count"] = pluginCatalogResult.PluginCount.ToString();
                    metadata["probe_plugin_sync_utc"] = DateTimeOffset.UtcNow.ToString("O");
                }
                else if (!string.IsNullOrWhiteSpace(pluginCatalogResult.ErrorCode))
                {
                    metadata["probe_plugin_error"] = pluginCatalogResult.ErrorCode;
                }
            }
            else
            {
                metadata[ProbeLastErrorMetadataKey] = probeResult.ErrorCode;
                if (TryParseDashboardPortFromProbeError(probeResult.ErrorCode, out var dashboardPort))
                {
                    metadata["probe_suggested_dashboard_port"] = dashboardPort.ToString();
                }
                unreachableCount++;
            }

            var updatedServer = KnownServer.Create(
                serverId: knownServer.ServerId,
                name: knownServer.Name,
                host: knownServer.Host,
                port: knownServer.Port,
                useTls: knownServer.UseTls,
                metadata: metadata,
                createdAtUtc: knownServer.CreatedAtUtc,
                updatedAtUtc: DateTimeOffset.UtcNow);

            await _storage.UpsertKnownServerAsync(updatedServer, cancellationToken);
            _logger.Log(LogLevel.Information, "startup_server_probe_result", "Startup probe completed for known server.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["host"] = knownServer.Host,
                    ["port"] = knownServer.Port.ToString(),
                    ["reachable"] = probeResult.IsReachable.ToString(),
                    ["attempts"] = probeResult.Attempts.ToString(),
                    ["error"] = probeResult.ErrorCode
                });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedServerId = SelectedServer?.ServerId;
            LoadServersFromStorage();
            if (!string.IsNullOrWhiteSpace(selectedServerId))
            {
                SelectedServer = FindServerById(selectedServerId);
            }

            TriggerServerAccessRefresh();
        });

        return (reachableCount, unreachableCount);
    }

    private bool TryBeginProbeCycle()
    {
        lock (_serverProbeLock)
        {
            if (_isServerProbeInProgress)
            {
                return false;
            }

            IsServerProbeInProgress = true;
            return true;
        }
    }

    private void EndProbeCycle()
    {
        lock (_serverProbeLock)
        {
            IsServerProbeInProgress = false;
        }
    }

    private static async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerWithRetryAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= ServerProbeMaxAttempts; attempt++)
        {
            var singleResult = await ProbeKnownServerOnceAsync(knownServer, cancellationToken);
            if (singleResult.IsReachable)
            {
                return (true, attempt, string.Empty);
            }

            lastError = singleResult.ErrorCode;
            if (attempt < ServerProbeMaxAttempts)
            {
                await Task.Delay(ServerProbeRetryDelayMs, cancellationToken);
            }
        }

        return (false, ServerProbeMaxAttempts, lastError ?? "probe_failed");
    }

    private static async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerOnceAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ServerProbeTimeoutMs);

        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(knownServer.Host, knownServer.Port, probeCts.Token);

            var dashboardPortHint = await TryReadBotPortDashboardHintAsync(socket, cancellationToken);
            if (dashboardPortHint is not null)
            {
                return (false, 1, $"bot_port_dashboard_{dashboardPortHint.Value}");
            }

            return (true, 1, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, 1, "timeout");
        }
        catch (SocketException socketException)
        {
            return (false, 1, $"socket_{socketException.SocketErrorCode}".ToLowerInvariant());
        }
        catch
        {
            return (false, 1, "connect_failed");
        }
    }

    private static bool TryParseDashboardPortFromProbeError(string? errorCode, out int dashboardPort)
    {
        dashboardPort = 0;
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return false;
        }

        const string prefix = "bot_port_dashboard_";
        if (!errorCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawPort = errorCode[prefix.Length..];
        return int.TryParse(rawPort, out dashboardPort) && dashboardPort is >= 1 and <= 65535;
    }

    private static async Task<int?> TryReadBotPortDashboardHintAsync(TcpClient socket, CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(BotWelcomeReadTimeoutMs);

        try
        {
            var stream = socket.GetStream();
            var buffer = new byte[2048];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
            if (bytesRead <= 0)
            {
                return null;
            }

            var line = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var firstLine = line.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            return BotPortWelcomeHintParser.TryExtractDashboardPort(firstLine, out var dashboardPort)
                ? dashboardPort
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }


    private void TriggerSelectedServerCatalogRefresh(ServerSummaryItem server)
    {
        if (!ShouldRefreshServerCatalog(server.ServerId))
        {
            return;
        }

        _ = RefreshSelectedServerCatalogAsync(server.ServerId);
    }

    private bool ShouldRefreshServerCatalog(string serverId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_serverCatalogRefreshLock)
        {
            if (_serverCatalogRefreshInFlight.Contains(serverId))
            {
                return false;
            }

            if (_serverCatalogLastRefreshUtc.TryGetValue(serverId, out var lastRefresh) &&
                now - lastRefresh < TimeSpan.FromMilliseconds(ServerCatalogSelectionRefreshCooldownMs))
            {
                return false;
            }

            _serverCatalogRefreshInFlight.Add(serverId);
            return true;
        }
    }

    private void MarkServerCatalogRefreshComplete(string serverId)
    {
        lock (_serverCatalogRefreshLock)
        {
            _serverCatalogRefreshInFlight.Remove(serverId);
            _serverCatalogLastRefreshUtc[serverId] = DateTimeOffset.UtcNow;
        }
    }

    private async Task RefreshSelectedServerCatalogAsync(string serverId)
    {
        try
        {
            var knownServer = (await _storage.ListKnownServersAsync()).FirstOrDefault(s => s.ServerId == serverId);
            if (knownServer is null)
            {
                return;
            }

            var result = await RefreshServerPluginCatalogCacheAsync(knownServer, CancellationToken.None);
            if (!result.Updated)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedServerId = SelectedServer?.ServerId;
                LoadServersFromStorage();
                if (!string.IsNullOrWhiteSpace(selectedServerId))
                {
                    SelectedServer = FindServerById(selectedServerId);
                }
            });
        }
        finally
        {
            MarkServerCatalogRefreshComplete(serverId);
        }
    }

    private async Task<(bool Updated, int PluginCount, string ErrorCode)> RefreshServerPluginCatalogCacheAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var fetchResult = await FetchServerPluginCatalogAsync(knownServer, cancellationToken);
        if (!fetchResult.Succeeded)
        {
            _logger.Log(LogLevel.Warning, "server_plugin_catalog_fetch_failed", "Failed to fetch server plugin catalog.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["reason"] = fetchResult.ErrorCode
                });

            return (false, 0, fetchResult.ErrorCode);
        }

        var cache = ServerPluginCache.Create(knownServer.ServerId, fetchResult.Plugins, DateTimeOffset.UtcNow);
        await _storage.UpsertServerPluginCacheAsync(cache, cancellationToken);

        _logger.Log(LogLevel.Information, "server_plugin_catalog_cached", "Server plugin catalog refreshed from probe.",
            new Dictionary<string, string>
            {
                ["server_id"] = knownServer.ServerId,
                ["plugin_count"] = cache.Plugins.Count.ToString(),
                ["source"] = fetchResult.Source
            });

        return (true, cache.Plugins.Count, string.Empty);
    }

    private async Task<(bool Succeeded, IReadOnlyList<PluginDescriptor> Plugins, string ErrorCode, string Source)> FetchServerPluginCatalogAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        var observedErrors = new List<string>();

        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var apiCatalogUri = new Uri(endpoint + "/api/game-catalog");
                using var apiResponse = await _serverCatalogHttpClient.GetAsync(apiCatalogUri, cancellationToken);
                if (apiResponse.IsSuccessStatusCode)
                {
                    var jsonPayload = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (ServerPluginCatalogParser.TryParseFromJsonCatalog(jsonPayload, out var plugins, out _))
                    {
                        return (true, plugins, string.Empty, "api_game_catalog");
                    }

                    observedErrors.Add($"api_parse_failed_{endpoint}");
                }
                else
                {
                    observedErrors.Add($"api_http_{(int)apiResponse.StatusCode}_{endpoint}");
                }
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"api_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"api_http_error_{endpoint}");
            }

            try
            {
                using var dashboardResponse = await _serverCatalogHttpClient.GetAsync(new Uri(endpoint + "/"), cancellationToken);
                if (!dashboardResponse.IsSuccessStatusCode)
                {
                    observedErrors.Add($"dashboard_http_{(int)dashboardResponse.StatusCode}_{endpoint}");
                    continue;
                }

                var html = await dashboardResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!ServerPluginCatalogParser.TryParseFromDashboardHtml(html, out var plugins, out _))
                {
                    observedErrors.Add($"dashboard_parse_failed_{endpoint}");
                    continue;
                }

                return (true, plugins, string.Empty, "dashboard_html");
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"dashboard_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"dashboard_http_error_{endpoint}");
            }
            catch
            {
                observedErrors.Add($"dashboard_fetch_failed_{endpoint}");
            }
        }

        var errorCode = observedErrors.Count == 0
            ? "plugin_catalog_fetch_failed"
            : $"plugin_catalog_fetch_failed:{string.Join(',', observedErrors)}";

        return (false, Array.Empty<PluginDescriptor>(), errorCode, "dashboard_html");
    }

    private static IReadOnlyList<string> BuildServerBaseEndpointCandidates(KnownServer knownServer)
    {
        var preferredScheme = knownServer.UseTls ? "https" : "http";
        var alternateScheme = knownServer.UseTls ? "http" : "https";
        var endpoints = new List<string>();

        var metadataDashboardEndpoint = FirstNonEmptyMetadataValue(knownServer.Metadata, DashboardEndpointMetadataKeys);
        if (Uri.TryCreate(metadataDashboardEndpoint, UriKind.Absolute, out var dashboardUri))
        {
            endpoints.Add(BuildBaseEndpoint(dashboardUri.Scheme, dashboardUri.Host, dashboardUri.Port));
        }

        var metadataDashboardPort = ParseDashboardPort(knownServer.Metadata);
        if (metadataDashboardPort is not null)
        {
            endpoints.Add(BuildBaseEndpoint(preferredScheme, knownServer.Host, metadataDashboardPort.Value));
            endpoints.Add(BuildBaseEndpoint(alternateScheme, knownServer.Host, metadataDashboardPort.Value));
        }

        endpoints.Add(BuildBaseEndpoint(preferredScheme, knownServer.Host, knownServer.Port));
        endpoints.Add(BuildBaseEndpoint(alternateScheme, knownServer.Host, knownServer.Port));

        if (knownServer.Port != DashboardPortFallback)
        {
            endpoints.Add(BuildBaseEndpoint(preferredScheme, knownServer.Host, DashboardPortFallback));
            endpoints.Add(BuildBaseEndpoint(alternateScheme, knownServer.Host, DashboardPortFallback));
        }

        return endpoints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int? ParseDashboardPort(IReadOnlyDictionary<string, string> metadata)
    {
        var raw = FirstNonEmptyMetadataValue(metadata, DashboardPortMetadataKeys);
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        return port;
    }

    private static string? FirstNonEmptyMetadataValue(IReadOnlyDictionary<string, string> metadata, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string BuildBaseEndpoint(string scheme, string host, int port)
    {
        return $"{scheme}://{host}:{port}";
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
            ServerAccessOwnerToken = resolved.IsValid ? MaskToken(resolved.OwnerToken) : "-";
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
        ((RelayCommand)CreateArenaStubCommand).RaiseCanExecuteChanged();
        ((RelayCommand)JoinArenaStubCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
    }

    private ServerAccessMetadata ResolveServerAccessMetadata(string? selectedServerId, string? selectedBotId)
    {
        if (!string.IsNullOrWhiteSpace(selectedBotId) &&
            TryGetActiveServerAccess(selectedBotId, out var activeServerId, out var activeAccess) &&
            (string.IsNullOrWhiteSpace(selectedServerId) || string.Equals(selectedServerId, activeServerId, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(activeAccess.OwnerToken))
            {
                return ServerAccessMetadata.Invalid("Owner token pending from active bot session.");
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
                if (candidateState?.IsArmed == true)
                {
                    profile = candidate;
                    runtimeState = candidateState;
                    break;
                }
            }
        }

        return ServerAccessMetadataResolver.Resolve(profile, runtimeState, selectedServerId);
    }

    private void SetActiveServerAccess(string botId, string serverId, string ownerToken, string dashboardEndpoint)
    {
        var access = string.IsNullOrWhiteSpace(ownerToken)
            ? ServerAccessMetadata.Invalid("Owner token pending from active bot session.")
            : new ServerAccessMetadata(
                IsValid: true,
                OwnerToken: ownerToken,
                DashboardEndpoint: dashboardEndpoint,
                StatusMessage: "Server access metadata loaded from active bot session.",
                Source: "active-session-cache");

        lock (_activeAccessCacheLock)
        {
            _activeServerAccessByBotId[botId] = (serverId, access);
        }
    }

    private void ClearActiveServerAccess(string botId)
    {
        lock (_activeAccessCacheLock)
        {
            _activeServerAccessByBotId.Remove(botId);
        }
    }

    private bool TryGetActiveServerAccess(string botId, out string serverId, out ServerAccessMetadata access)
    {
        lock (_activeAccessCacheLock)
        {
            if (_activeServerAccessByBotId.TryGetValue(botId, out var value))
            {
                serverId = value.ServerId;
                access = value.Access;
                return true;
            }
        }

        serverId = string.Empty;
        access = ServerAccessMetadata.Invalid("No active session metadata cached.");
        return false;
    }

    private static string MaskToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length <= 8)
        {
            return "********";
        }

        return $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    private static IReadOnlyList<string> ParseArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Dictionary<string, string> ParseMetadata(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var pairs = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                result[parts[0]] = parts[1];
            }
        }

        return result;
    }

    private static string FormatMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in metadata)
        {
            parts.Add($"{item.Key}={item.Value}");
        }

        return string.Join(';', parts);
    }

}

public sealed class BotSummaryItem
{
    private static readonly IBrush DefaultAccentBrush = new SolidColorBrush(Color.Parse("#0e7a6d"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#fffaf3"));
    private static readonly IBrush ArmedAccentBrush = new SolidColorBrush(Color.Parse("#b7791f"));
    private static readonly IBrush ArmedBackgroundBrush = new SolidColorBrush(Color.Parse("#fff4df"));
    private static readonly IBrush ActiveAccentBrush = new SolidColorBrush(Color.Parse("#2b8a3e"));
    private static readonly IBrush ActiveBackgroundBrush = new SolidColorBrush(Color.Parse("#e8f8ec"));
    private static readonly IBrush ErrorAccentBrush = new SolidColorBrush(Color.Parse("#c92a2a"));
    private static readonly IBrush ErrorBackgroundBrush = new SolidColorBrush(Color.Parse("#fdecec"));

    public required string BotId { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required IBrush AccentBrush { get; init; }
    public required IBrush BackgroundBrush { get; init; }
    public required string LaunchPath { get; init; }
    public string? AvatarImagePath { get; init; }
    public required IReadOnlyList<string> LaunchArgs { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public BotCardVisualState VisualState { get; init; }
    public AgentLifecycleState LifecycleState { get; init; }
    public bool IsArmed { get; init; }
    public string? LastErrorCode { get; init; }
    public required ICommand ArmCommand { get; init; }
    public required ICommand DisarmCommand { get; init; }
    public required ICommand DeployCommand { get; init; }
    public bool CanArm => !IsArmed;
    public bool CanDisarm => IsArmed;

    public static BotSummaryItem FromProfile(
        BotProfile profile,
        AgentRuntimeState? runtimeState,
        ICommand armCommand,
        ICommand disarmCommand,
        ICommand deployCommand)
    {
        var visualState = BotCardVisualStateRules.Resolve(runtimeState);
        var status = BuildStatusText(runtimeState, visualState);
        var (accentBrush, backgroundBrush) = ResolveBrushes(visualState);

        return new BotSummaryItem
        {
            BotId = profile.BotId,
            Name = profile.Name,
            Summary = "Local profile",
            Status = status,
            AccentBrush = accentBrush,
            BackgroundBrush = backgroundBrush,
            LaunchPath = profile.LaunchPath,
            AvatarImagePath = profile.AvatarImagePath,
            LaunchArgs = profile.LaunchArgs,
            Metadata = profile.Metadata,
            CreatedAtUtc = profile.CreatedAtUtc,
            VisualState = visualState,
            LifecycleState = runtimeState?.LifecycleState ?? AgentLifecycleState.Unknown,
            IsArmed = runtimeState?.IsArmed ?? false,
            LastErrorCode = runtimeState?.LastErrorCode,
            ArmCommand = armCommand,
            DisarmCommand = disarmCommand,
            DeployCommand = deployCommand
        };
    }

    private static string BuildStatusText(AgentRuntimeState? runtimeState, BotCardVisualState visualState)
    {
        if (runtimeState is null)
        {
            return "Registered";
        }

        return visualState switch
        {
            BotCardVisualState.Armed => "Armed",
            BotCardVisualState.ActiveSession => "Active Session",
            BotCardVisualState.Error => string.IsNullOrWhiteSpace(runtimeState.LastErrorCode)
                ? "Error"
                : $"Error: {runtimeState.LastErrorCode}",
            _ => runtimeState.LifecycleState.ToString()
        };
    }

    private static (IBrush AccentBrush, IBrush BackgroundBrush) ResolveBrushes(BotCardVisualState visualState)
    {
        return visualState switch
        {
            BotCardVisualState.Armed => (ArmedAccentBrush, ArmedBackgroundBrush),
            BotCardVisualState.ActiveSession => (ActiveAccentBrush, ActiveBackgroundBrush),
            BotCardVisualState.Error => (ErrorAccentBrush, ErrorBackgroundBrush),
            _ => (DefaultAccentBrush, DefaultBackgroundBrush)
        };
    }

    public BotProfile ToProfile()
    {
        return BotProfile.Create(
            botId: BotId,
            name: Name,
            launchPath: LaunchPath,
            avatarImagePath: AvatarImagePath,
            launchArgs: LaunchArgs,
            metadata: new Dictionary<string, string>(Metadata),
            createdAtUtc: CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);
    }
}

public sealed class ServerSummaryItem
{
    private static readonly IBrush LiveAccentBrush = new SolidColorBrush(Color.Parse("#2b8a3e"));
    private static readonly IBrush LiveBackgroundBrush = new SolidColorBrush(Color.Parse("#e8f8ec"));
    private static readonly IBrush InactiveAccentBrush = new SolidColorBrush(Color.Parse("#6c757d"));
    private static readonly IBrush InactiveBackgroundBrush = new SolidColorBrush(Color.Parse("#f1f3f5"));

    public required string ServerId { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool UseTls { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required IReadOnlyList<PluginDescriptor> CachedPlugins { get; init; }
    public required string Endpoint { get; init; }
    public required string Status { get; init; }
    public required IBrush AccentBrush { get; init; }
    public required IBrush BackgroundBrush { get; init; }
    public required ServerCardVisualState VisualState { get; init; }
    public int PluginCount => CachedPlugins.Count;

    public static ServerSummaryItem FromKnownServer(KnownServer server, ServerPluginCache? cache)
    {
        var scheme = server.UseTls ? "https" : "http";
        var endpoint = $"{scheme}://{server.Host}:{server.Port}";
        var plugins = cache?.Plugins ?? Array.Empty<PluginDescriptor>();
        var visualState = ServerCardVisualStateRules.Resolve(server.Metadata);
        var (accentBrush, backgroundBrush) = ResolveBrushes(visualState);
        var status = BuildProbeAwareStatus(server.Metadata, cache);

        return new ServerSummaryItem
        {
            ServerId = server.ServerId,
            Name = server.Name,
            Host = server.Host,
            Port = server.Port,
            UseTls = server.UseTls,
            CreatedAtUtc = server.CreatedAtUtc,
            Metadata = server.Metadata,
            CachedPlugins = plugins,
            Endpoint = endpoint,
            Status = status,
            AccentBrush = accentBrush,
            BackgroundBrush = backgroundBrush,
            VisualState = visualState
        };
    }

    private static (IBrush AccentBrush, IBrush BackgroundBrush) ResolveBrushes(ServerCardVisualState visualState)
    {
        return visualState switch
        {
            ServerCardVisualState.Live => (LiveAccentBrush, LiveBackgroundBrush),
            _ => (InactiveAccentBrush, InactiveBackgroundBrush)
        };
    }

    private static string BuildProbeAwareStatus(IReadOnlyDictionary<string, string> metadata, ServerPluginCache? cache)
    {
        if (metadata.TryGetValue("probe_status", out var probeStatus))
        {
            if (string.Equals(probeStatus, "reachable", StringComparison.OrdinalIgnoreCase))
            {
                return "Status: reachable";
            }

            if (metadata.TryGetValue("probe_last_error", out var probeError) &&
                !string.IsNullOrWhiteSpace(probeError) &&
                probeError.StartsWith("bot_port_dashboard_", StringComparison.OrdinalIgnoreCase))
            {
                var suggestedPort = probeError["bot_port_dashboard_".Length..];
                if (!string.IsNullOrWhiteSpace(suggestedPort))
                {
                    return $"Status: wrong endpoint (bot port). Use dashboard port {suggestedPort}";
                }
            }

            var errorSuffix = metadata.TryGetValue("probe_last_error", out var errorValue) && !string.IsNullOrWhiteSpace(errorValue)
                ? $" ({errorValue})"
                : string.Empty;
            return $"Status: unreachable{errorSuffix}";
        }

        return cache is null
            ? "Status: pending probe"
            : "Status: pending startup probe";
    }
}

public sealed record ServerMetadataEntryItem(string Key, string Value);

public sealed record ServerPluginCatalogItem(
    string Name,
    string DisplayName,
    string Version,
    IReadOnlyDictionary<string, string>? PluginMetadata = null)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        PluginMetadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RegisterHandshakeResult(
    string SessionId,
    string ServerBotId,
    string ServerBotSecret,
    string OwnerToken,
    string DashboardEndpoint);

public sealed record AgentControlResponse(
    string Type,
    string Id,
    string Message,
    string SessionId,
    string BotId,
    string BotSecret,
    string OwnerToken,
    string DashboardEndpoint,
    string DashboardHost,
    string DashboardPort);


public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
