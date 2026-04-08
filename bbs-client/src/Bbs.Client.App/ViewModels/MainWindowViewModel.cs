using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
        "dashboard_endpoint"
    };

    private static readonly string[] DashboardPortMetadataKeys =
    {
        "dashboard_port"
    };

    private readonly IClientLogger _logger;
    private readonly PersonaManager? _personaManager;
    private string? _currentPersonaPath;
    private IClientStorage _storage;
    private IBotOrchestrationService _orchestration;
    private readonly HttpClient _serverCatalogHttpClient;
    private readonly UIStateViewModel _uiState = new();
    private readonly BotServiceViewModel _botService = new();
    private readonly DeploymentServiceViewModel _deploymentService = null!;
    private ServerServiceViewModel _serverService = null!;  // Initialized in constructor with dependencies
    private readonly ArenaServiceViewModel _arenaService = new();
    private readonly SessionServiceViewModel _sessionService = new();
    private readonly ServerAccessServiceViewModel _serverAccessService = new();
    private BotSummaryItem? _selectedBot;
    private int _serverAccessRefreshVersion;

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

        _serverService = new ServerServiceViewModel(_storage, _logger, _serverCatalogHttpClient);
        _arenaService.SetPluginCatalog(_serverService.ServerPluginCatalogEntries);
        _serverService.ServerPluginCatalogEntries.CollectionChanged += OnServerPluginCatalogEntriesChanged;
        _deploymentService = new DeploymentServiceViewModel(_storage, _orchestration, _logger, _sessionService);

        Bots = new ObservableCollection<BotSummaryItem>();
        ServerArenaEntries = new ObservableCollection<ServerArenaItem>();

        _uiState.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(UIStateViewModel.ShowBotEditor) ||
                e.PropertyName == nameof(UIStateViewModel.ShowServerEditor) ||
                e.PropertyName == nameof(UIStateViewModel.ShowServerDetails) ||
                e.PropertyName == nameof(UIStateViewModel.ShowArenaViewer) ||
                e.PropertyName == nameof(UIStateViewModel.IsLeftPanelExpanded) ||
                e.PropertyName == nameof(UIStateViewModel.IsRightPanelExpanded) ||
                e.PropertyName == nameof(UIStateViewModel.IsLeftPanelCollapsed) ||
                e.PropertyName == nameof(UIStateViewModel.IsRightPanelCollapsed) ||
                e.PropertyName == nameof(UIStateViewModel.LeftPanelWidth) ||
                e.PropertyName == nameof(UIStateViewModel.RightPanelWidth) ||
                e.PropertyName == nameof(UIStateViewModel.CurrentContextLabel))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        _serverService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ServerServiceViewModel.SelectedServer))
            {
                OnPropertyChanged(nameof(SelectedServer));
                OnPropertyChanged(nameof(HasSelectedServer));
                OnPropertyChanged(nameof(ServerDetailEndpoint));
                OnPropertyChanged(nameof(ServerDetailProbeStatus));

                RefreshSelectedServerDetail();
                TriggerServerAccessRefresh();
                RefreshContextProjection();

                if (SetServerContextCommand is RelayCommand setServerContextCommand)
                {
                    setServerContextCommand.RaiseCanExecuteChanged();
                }

                if (RefreshServerArenasCommand is RelayCommand refreshServerArenasCommand)
                {
                    refreshServerArenasCommand.RaiseCanExecuteChanged();
                }

                if (DeploySelectedBotCommand is RelayCommand deploySelectedBotCommand)
                {
                    deploySelectedBotCommand.RaiseCanExecuteChanged();
                }

                if (DeployBotFromCardCommand is RelayCommand<BotSummaryItem> deployBotFromCardCommand)
                {
                    deployBotFromCardCommand.RaiseCanExecuteChanged();
                }
            }

            if (e.PropertyName == nameof(ServerServiceViewModel.IsServerProbeInProgress) ||
                e.PropertyName == nameof(ServerServiceViewModel.IsServerDetailLoading) ||
                e.PropertyName == nameof(ServerServiceViewModel.ServerCatalogStatus) ||
                e.PropertyName == nameof(ServerServiceViewModel.HasSelectedServer) ||
                e.PropertyName == nameof(ServerServiceViewModel.HasServerMetadata) ||
                e.PropertyName == nameof(ServerServiceViewModel.HasServerPluginCatalog) ||
                e.PropertyName == nameof(ServerServiceViewModel.ShowServerMetadataEmpty) ||
                e.PropertyName == nameof(ServerServiceViewModel.ShowServerPluginCatalogEmpty))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        _arenaService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ArenaServiceViewModel.OwnerArenaSelectedPlugin) ||
                e.PropertyName == nameof(ArenaServiceViewModel.OwnerArenaArgs) ||
                e.PropertyName == nameof(ArenaServiceViewModel.OwnerArenaTimeMs) ||
                e.PropertyName == nameof(ArenaServiceViewModel.OwnerArenaAllowHandicap) ||
                e.PropertyName == nameof(ArenaServiceViewModel.OwnerJoinArenaId) ||
                e.PropertyName == nameof(ArenaServiceViewModel.OwnerJoinHandicapPercent))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        _sessionService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SessionServiceViewModel.HasActiveBotSessions) ||
                e.PropertyName == nameof(SessionServiceViewModel.ShowActiveBotSessionsEmpty))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        _serverAccessService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ServerAccessServiceViewModel.ServerAccessMetadata) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.IsServerAccessLoading) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.ServerAccessStatus) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.ServerAccessOwnerToken) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.ServerAccessDashboardEndpoint) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.OwnerTokenActionStatus) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.HasValidServerAccess) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.ShowOwnerTokenActions) ||
                e.PropertyName == nameof(ServerAccessServiceViewModel.ShowOwnerTokenActionsUnavailable))
            {
                OnPropertyChanged(e.PropertyName);
                RefreshOwnerTokenActionProjection();
            }
        };

        ToggleLeftPanelCommand = _uiState.ToggleLeftPanelCommand;
        ToggleRightPanelCommand = _uiState.ToggleRightPanelCommand;
        SetHomeContextCommand = _uiState.SetHomeContextCommand;
        SetBotContextCommand = new RelayCommand(SetBotContextFromSelection, () => SelectedBot is not null && IsPersonaLoaded);
        SetServerContextCommand = new RelayCommand(SetServerContextFromSelection, () => SelectedServer is not null && IsPersonaLoaded);
        OpenBotEditorCommand = new RelayCommand<BotSummaryItem>(OpenBotEditorFromCard);
        DeployBotFromCardCommand = new RelayCommand<BotSummaryItem>(DeployBotFromCard, bot => bot is not null && CanDeploySelectedBot());
        ActivateServerCardCommand = new RelayCommand<ServerSummaryItem>(ActivateServerCardFromPanel);
        OpenServerEditorFromCardCommand = new RelayCommand<ServerSummaryItem>(OpenServerEditorFromCard);
        SaveBotProfileCommand = new RelayCommand(SaveBotProfile);
        StartNewBotCommand = new RelayCommand(StartNewBot);
        DeploySelectedBotCommand = new RelayCommand(DeploySelectedBotToSelectedServer, CanDeploySelectedBot);
        SaveServerProfileCommand = new RelayCommand(SaveServerProfile);
        StartNewServerCommand = new RelayCommand(StartNewServer);
        ReprobeServersCommand = new RelayCommand(ReprobeServers, () => !IsServerProbeInProgress && Servers.Count > 0 && IsPersonaLoaded);
        RefreshServerAccessCommand = new RelayCommand(RefreshServerAccessMetadata);
        CreateArenaCommand = new RelayCommand(ExecuteCreateArena, CanExecuteOwnerTokenAction);
        JoinArenaCommand = new RelayCommand(ExecuteJoinArena, CanExecuteOwnerTokenAction);
        RefreshServerArenasCommand = new RelayCommand(RefreshServerArenas, CanRefreshServerArenas);
        OpenArenaViewerInBrowserCommand = new RelayCommand(OpenArenaViewerInBrowser, () => !string.IsNullOrWhiteSpace(ArenaViewerUrl));

        ConfigureEmbeddedViewerSupport(embeddedViewerAvailable, embeddedViewerMessage);

        _logger.Log(LogLevel.Information, "mainvm_init", "MainWindowViewModel constructor initialized.", null);
    }

    public UIStateViewModel UIState => _uiState;

    public BotServiceViewModel BotService => _botService;
    public DeploymentServiceViewModel DeploymentService => _deploymentService;
    public ServerServiceViewModel ServerService => _serverService;
    public ArenaServiceViewModel ArenaService => _arenaService;
    public SessionServiceViewModel SessionService => _sessionService;

    public string WorkspaceTitle { get; private set; } = "";
    public string WorkspaceDescription { get; private set; } = "";

    public string CurrentTitleText => _uiState.CurrentContext switch
    {
        WorkspaceContext.BotDetails => SelectedBot is null ? "Bot Context" : $"{SelectedBot.Name}",
        WorkspaceContext.ServerDetails => SelectedServer is null ? "Server Context" : $"{SelectedServer.Name}",
        WorkspaceContext.ServerEditor => SelectedServer is null ? "Edit Server" : $"Edit {SelectedServer.Name}",
        WorkspaceContext.ArenaViewer => string.IsNullOrWhiteSpace(ArenaViewerLabel) ? "Arena Viewer" : ArenaViewerLabel,
        _ => "BBS"
    };
    public bool IsServerDetailLoading
    {
        get => _serverService.IsServerDetailLoading;
    }

    public bool IsServerAccessLoading
    {
        get => _serverAccessService.IsServerAccessLoading;
        private set => _serverAccessService.IsServerAccessLoading = value;
    }

    public bool HasSelectedServer => SelectedServer is not null;
    public bool HasServerMetadata => _serverService.ServerMetadataEntries.Count > 0;
    public bool HasServerPluginCatalog => _serverService.ServerPluginCatalogEntries.Count > 0;
    public bool ShowServerMetadataEmpty => _serverService.ShowServerMetadataEmpty;
    public bool ShowServerPluginCatalogEmpty => _serverService.ShowServerPluginCatalogEmpty;
    public bool HasValidServerAccess => _serverAccessService.HasValidServerAccess;
    public bool ShowOwnerTokenActions => _serverAccessService.ShowOwnerTokenActions;
    public bool ShowOwnerTokenActionsUnavailable => _serverAccessService.ShowOwnerTokenActionsUnavailable;
    public string ServerDetailEndpoint => SelectedServer?.Endpoint ?? "-";
    public string ServerDetailProbeStatus => SelectedServer?.Status ?? "Status: not available";
    public string ServerAccessStatus
    {
        get => _serverAccessService.ServerAccessStatus;
        private set => _serverAccessService.ServerAccessStatus = value;
    }

    public string ServerAccessOwnerToken
    {
        get => _serverAccessService.ServerAccessOwnerToken;
        private set => _serverAccessService.ServerAccessOwnerToken = value;
    }

    public string ServerAccessDashboardEndpoint
    {
        get => _serverAccessService.ServerAccessDashboardEndpoint;
        private set => _serverAccessService.ServerAccessDashboardEndpoint = value;
    }

    public ServerAccessMetadata ServerAccessMetadata
    {
        get => _serverAccessService.ServerAccessMetadata;
        private set => _serverAccessService.ServerAccessMetadata = value;
    }

    public string OwnerTokenActionStatus
    {
        get => _serverAccessService.OwnerTokenActionStatus;
        private set => _serverAccessService.OwnerTokenActionStatus = value;
    }

    public string OwnerArenaSelectedPlugin
    {
        get => _arenaService.OwnerArenaSelectedPlugin;
        set => _arenaService.OwnerArenaSelectedPlugin = value;
    }

    public string OwnerArenaArgs
    {
        get => _arenaService.OwnerArenaArgs;
        set => _arenaService.OwnerArenaArgs = value;
    }

    public string OwnerArenaTimeMs
    {
        get => _arenaService.OwnerArenaTimeMs;
        set => _arenaService.OwnerArenaTimeMs = value;
    }

    public bool OwnerArenaAllowHandicap
    {
        get => _arenaService.OwnerArenaAllowHandicap;
        set => _arenaService.OwnerArenaAllowHandicap = value;
    }

    public string OwnerJoinArenaId
    {
        get => _arenaService.OwnerJoinArenaId;
        set => _arenaService.OwnerJoinArenaId = value;
    }

    public string OwnerJoinHandicapPercent
    {
        get => _arenaService.OwnerJoinHandicapPercent;
        set => _arenaService.OwnerJoinHandicapPercent = value;
    }

    public string ServerCatalogStatus => _serverService.ServerCatalogStatus;

    public bool IsServerProbeInProgress => _serverService.IsServerProbeInProgress;

    public bool ShowBotEditor => _uiState.ShowBotEditor;
    public bool ShowServerEditor => _uiState.ShowServerEditor;
    public bool ShowServerDetails => _uiState.ShowServerDetails;
    public bool ShowArenaViewer => _uiState.ShowArenaViewer;

    public ObservableCollection<BotSummaryItem> Bots { get; }
    public ObservableCollection<ServerSummaryItem> Servers => _serverService.Servers;
    public ObservableCollection<ServerMetadataEntryItem> ServerMetadataEntries => _serverService.ServerMetadataEntries;
    public ObservableCollection<ServerPluginCatalogItem> ServerPluginCatalogEntries => _serverService.ServerPluginCatalogEntries;
    public ObservableCollection<ServerArenaItem> ServerArenaEntries { get; }

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
            ((RelayCommand<BotSummaryItem>)DeployBotFromCardCommand).RaiseCanExecuteChanged();
            RefreshActiveBotSessionsProjection();
            TriggerServerAccessRefresh();
        }
    }

    public ServerSummaryItem? SelectedServer
    {
        get => _serverService.SelectedServer;
        set
        {
            if (_serverService.SelectedServer == value)
            {
                return;
            }

            _serverService.SelectedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
            RefreshSelectedServerDetail();
            ((RelayCommand)SetServerContextCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RefreshServerArenasCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
            ((RelayCommand<BotSummaryItem>)DeployBotFromCardCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                StopArenaViewerWatch();
                _uiState.SwitchContext(WorkspaceContext.ServerDetails);
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
    public ICommand OpenBotEditorCommand { get; }
    public ICommand DeployBotFromCardCommand { get; }
    public ICommand ActivateServerCardCommand { get; }
    public ICommand OpenServerEditorFromCardCommand { get; }
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

    private void ReprobeServers()
    {
        _serverService.TriggerManualProbe();
    }

    private void RefreshServerAccessMetadata()
    {
        TriggerServerAccessRefresh();
    }

    private bool CanExecuteOwnerTokenAction()
    {
        return _serverAccessService.HasValidServerAccess && !_serverAccessService.IsServerAccessLoading && SelectedServer is not null;
    }

    private bool CanRefreshServerArenas()
    {
        return IsPersonaLoaded &&
               !IsServerArenasLoading &&
               SelectedServer?.VisualState == ServerCardVisualState.Live;
    }

    private bool CanDeploySelectedBot()
    {
        if (!IsPersonaLoaded)
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



    private void SetBotContextFromSelection()
    {
        if (SelectedBot is not null)
        {
            PopulateBotEditor(SelectedBot);
        }

        SwitchWorkspaceContext(WorkspaceContext.BotDetails);
    }

    private void SetServerContextFromSelection()
    {
        if (SelectedServer is not null)
        {
            PopulateServerEditor(SelectedServer);
        }

        SwitchWorkspaceContext(WorkspaceContext.ServerDetails);
    }

    private void OpenBotEditorFromCard(BotSummaryItem? bot)
    {
        if (bot is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedBot, bot))
        {
            SelectedBot = bot;
        }

        PopulateBotEditor(bot);
        SwitchWorkspaceContext(WorkspaceContext.BotDetails);
    }

    private void OpenServerEditorFromCard(ServerSummaryItem? server)
    {
        if (server is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedServer, server))
        {
            SelectedServer = server;
        }

        PopulateServerEditor(server);
        SwitchWorkspaceContext(WorkspaceContext.ServerEditor);
    }

    private void DeployBotFromCard(BotSummaryItem? bot)
    {
        if (bot is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedBot, bot))
        {
            SelectedBot = bot;
        }

        DeploySelectedBotToSelectedServer();
    }

    private void ActivateServerCardFromPanel(ServerSummaryItem? server)
    {
        if (server is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedServer, server))
        {
            SelectedServer = server;
            return;
        }

        PopulateServerEditor(server);
        SwitchWorkspaceContext(WorkspaceContext.ServerDetails);
    }

    private void SwitchWorkspaceContext(WorkspaceContext context)
    {
        StopArenaViewerWatch();
        _uiState.SwitchContext(context);
        RefreshContextProjection();
    }

    private void RefreshContextProjection()
    {
        switch (_uiState.CurrentContext)
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
            case WorkspaceContext.ServerEditor:
                WorkspaceTitle = SelectedServer is null ? "Server Registration / Edit" : $"Edit Server: {SelectedServer.Name}";
                WorkspaceDescription = SelectedServer is null
                    ? "Select a server card and use the edit action to modify its registration details."
                    : "Update known-server registration fields and save changes from this editor view.";
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

        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
        OnPropertyChanged(nameof(CurrentTitleText));
        ((RelayCommand)DeploySelectedBotCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenArenaViewerInBrowserCommand).RaiseCanExecuteChanged();
    }

    private void StartNewBot()
    {
        SelectedBot = null;
        _uiState.SwitchContext(WorkspaceContext.BotDetails);
        _botService.PrepareForNewBot();
        RefreshContextProjection();
    }

    private void StartNewServer()
    {
        StopArenaViewerWatch();
        SelectedServer = null;
        _uiState.SwitchContext(WorkspaceContext.ServerEditor);
        _serverService.PrepareForNewServer();
        RefreshContextProjection();
    }

    private void SaveBotProfile()
    {
        var botId = SelectedBot?.BotId ?? Guid.NewGuid().ToString("N");
        var createdAt = SelectedBot?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.UtcNow;

        var profile = BotProfile.Create(
            botId: botId,
            name: _botService.BotEditorName.Trim(),
            launchPath: _botService.BotEditorLaunchPath.Trim(),
            avatarImagePath: SelectedBot?.AvatarImagePath,
            launchArgs: MainWindowViewModelHelpers.ParseArgs(_botService.BotEditorArgs),
            metadata: MainWindowViewModelHelpers.ParseMetadata(_botService.BotEditorMetadata),
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            _botService.BotEditorMessage = $"Cannot save bot: {string.Join(", ", errors)}";
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
        _botService.BotEditorMessage = $"Saved bot profile: {profile.Name}";
        _logger.Log(LogLevel.Information, "bot_profile_saved", "Bot profile persisted.",
            new Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["name"] = profile.Name
            });
    }

    public int ShutdownRuntimeSessionsForExit()
    {
        StopArenaViewerWatch();

        var sessions = _sessionService.GetAllActiveDeployConnections();

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
        var value = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(server.Metadata, DashboardEndpointMetadataKeys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var dashboardPort = MainWindowViewModelHelpers.ParsePositivePort(server.Metadata, DashboardPortMetadataKeys) ?? DashboardPortFallback;
        var scheme = server.UseTls ? "https" : "http";
        return MainWindowViewModelHelpers.BuildBaseEndpoint(scheme, server.Host, dashboardPort);
    }

    private void HandleOrchestrationException(string action, string botId, string errorCode, Exception ex)
    {
        if (string.Equals(action, "deploy", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(errorCode, "deploy_runtime_unavailable", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ex.Message))
        {
            _botService.BotEditorMessage = $"Deploy failed: {ex.Message}";
        }
        else
        {
            _botService.BotEditorMessage = $"Unable to {action} bot ({errorCode}).";
        }

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
        _serverService.LoadServersFromStorage();
        OnPropertyChanged(nameof(Servers));
        ((RelayCommand)ReprobeServersCommand).RaiseCanExecuteChanged();
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
        return _serverService.FindServerById(serverId);
    }

    private void PopulateBotEditor(BotSummaryItem bot)
    {
        _botService.PopulateEditor(bot);
    }

    private void PopulateServerEditor(ServerSummaryItem server)
    {
        _serverService.PopulateEditor(server);
    }

    private void RefreshSelectedServerDetail()
    {
        // Delegated to ServerService; now just handle arena refresh and plugin selection
        var server = SelectedServer;
        _serverService.RefreshSelectedServerDetail(server);
        if (server is null)
        {
            ServerArenaEntries.Clear();
            ServerArenasStatus = "Select a server to load active arenas.";
            return;
        }

        // Prevent stale arena cards from a previously selected server while new refresh is in-flight.
        ServerArenaEntries.Clear();
        ServerArenasStatus = "Loading active arenas...";
        _ = RefreshSelectedServerArenasAsync();
        _arenaService.EnsureValidPluginSelection(_serverService.ServerPluginCatalogEntries);
    }

    private void EnsureOwnerArenaPluginSelection()
    {
        _arenaService.EnsureValidPluginSelection(_serverService.ServerPluginCatalogEntries);
    }

    private void OnServerPluginCatalogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureOwnerArenaPluginSelection();
    }

    private void SaveServerProfile()
    {
        if (!int.TryParse(_serverService.ServerEditorPort.Trim(), out var parsedPort))
        {
            _serverService.ServerEditorMessage = "Cannot save server: server_port_invalid";
            return;
        }

        var serverId = SelectedServer?.ServerId ?? Guid.NewGuid().ToString("N");
        var createdAt = SelectedServer?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.UtcNow;

        var server = KnownServer.Create(
            serverId: serverId,
            name: _serverService.ServerEditorName.Trim(),
            host: _serverService.ServerEditorHost.Trim(),
            port: parsedPort,
            useTls: _serverService.ServerEditorUseTls,
            metadata: MainWindowViewModelHelpers.ParseMetadata(_serverService.ServerEditorMetadata),
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var serverErrors = server.Validate();
        if (serverErrors.Count > 0)
        {
            _serverService.ServerEditorMessage = $"Cannot save server: {string.Join(", ", serverErrors)}";
            return;
        }

        _storage.UpsertKnownServerAsync(server).GetAwaiter().GetResult();
        LoadServersFromStorage();
        SelectedServer = FindServerById(serverId);
        TriggerServerAccessRefresh();
        var dashboardPortHint = server.Port == BotTcpDefaultPort
            ? " Saved, but 8080 is commonly the bot TCP port; dashboard is often 3000."
            : string.Empty;
        _serverService.ServerEditorMessage = $"Saved known server: {server.Name}.{dashboardPortHint}";
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
            if (knownServerAccess.IsValid || _uiState.CurrentContext == WorkspaceContext.ServerDetails)
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

        var ownerToken = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(server.Metadata, new[]
        {
            ClientOwnerTokenMetadataKey,
            ServerAccessOwnerTokenMetadataKey
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

}
