using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
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
    private const int ServerCatalogFetchTimeoutMs = 2000;
    private const int ServerCatalogSelectionRefreshCooldownMs = 5000;
    private const int ServerProbeMaxAttempts = 2;
    private const int ServerProbeRetryDelayMs = 200;
    private const string ProbeStatusMetadataKey = "probe_status";
    private const string ProbeLastCheckedMetadataKey = "probe_last_checked_utc";
    private const string ProbeLastErrorMetadataKey = "probe_last_error";

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
    private string _serverEditorPort = "8080";
    private bool _serverEditorUseTls;
    private string _serverEditorMetadata = string.Empty;
    private string _serverEditorMessage = "Fill out the server form and save.";
    private bool _isServerProbeInProgress;
    private bool _isServerDetailLoading;
    private bool _isServerAccessLoading;
    private string _serverCatalogStatus = "Select a server to view cached plugin catalog.";
    private string _serverAccessStatus = "Select an armed bot session to load server access metadata.";
    private string _ownerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";
    private string _serverAccessOwnerToken = "-";
    private string _serverAccessDashboardEndpoint = "-";
    private int _serverAccessRefreshVersion;
    private readonly Dictionary<string, DateTimeOffset> _serverCatalogLastRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _serverCatalogRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _serverProbeLock = new();
    private readonly object _serverCatalogRefreshLock = new();

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
            if (value is not null)
            {
                PopulateBotEditor(value);
                _currentContext = WorkspaceContext.BotDetails;
                RefreshContextProjection();
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

    private void ExecuteCreateArenaStub()
    {
        ExecuteOwnerTokenActionStub(OwnerTokenActionType.CreateArena);
    }

    private void ExecuteJoinArenaStub()
    {
        ExecuteOwnerTokenActionStub(OwnerTokenActionType.JoinArena);
    }

    private void ExecuteOwnerTokenActionStub(OwnerTokenActionType actionType)
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

        OwnerTokenActionStatus = $"{guard.Message} Placeholder only: {guard.Plan.PlaceholderMethod} {guard.Plan.PlaceholderRoute}.";
        _logger.Log(LogLevel.Information, "owner_token_action_stub_invoked", "Owner-token action stub invoked.",
            new Dictionary<string, string>
            {
                ["action"] = actionType.ToString(),
                ["server_id"] = selectedServerId ?? "none",
                ["route"] = guard.Plan.PlaceholderRoute
            });
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
        ServerEditorPort = "8080";
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
            Bots.Add(BotSummaryItem.FromProfile(profile, runtimeState));
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
            ServerPluginCatalogEntries.Add(new ServerPluginCatalogItem(plugin.Name, plugin.DisplayName, plugin.Version));
        }

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
        ServerEditorMessage = $"Saved known server: {server.Name}";
        _logger.Log(LogLevel.Information, "known_server_saved", "Known server and plugin cache persisted.",
            new Dictionary<string, string>
            {
                ["server_id"] = server.ServerId,
                ["name"] = server.Name
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

        return new[]
        {
            $"{preferredScheme}://{knownServer.Host}:{knownServer.Port}",
            $"{alternateScheme}://{knownServer.Host}:{knownServer.Port}"
        };
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
    }

    private ServerAccessMetadata ResolveServerAccessMetadata(string? selectedServerId, string? selectedBotId)
    {
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
    public required IReadOnlyList<string> LaunchArgs { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public BotCardVisualState VisualState { get; init; }
    public AgentLifecycleState LifecycleState { get; init; }
    public bool IsArmed { get; init; }
    public string? LastErrorCode { get; init; }

    public static BotSummaryItem FromProfile(BotProfile profile, AgentRuntimeState? runtimeState)
    {
        var visualState = BotCardVisualStateRules.Resolve(runtimeState);
        var status = BuildStatusText(runtimeState, visualState);
        var (accentBrush, backgroundBrush) = ResolveBrushes(visualState);

        return new BotSummaryItem
        {
            BotId = profile.BotId,
            Name = profile.Name,
            Summary = $"Entry: {profile.LaunchPath}",
            Status = status,
            AccentBrush = accentBrush,
            BackgroundBrush = backgroundBrush,
            LaunchPath = profile.LaunchPath,
            LaunchArgs = profile.LaunchArgs,
            Metadata = profile.Metadata,
            CreatedAtUtc = profile.CreatedAtUtc,
            VisualState = visualState,
            LifecycleState = runtimeState?.LifecycleState ?? AgentLifecycleState.Unknown,
            IsArmed = runtimeState?.IsArmed ?? false,
            LastErrorCode = runtimeState?.LastErrorCode
        };
    }

    private static string BuildStatusText(AgentRuntimeState? runtimeState, BotCardVisualState visualState)
    {
        if (runtimeState is null)
        {
            return "State: registered";
        }

        return visualState switch
        {
            BotCardVisualState.Armed => $"State: armed ({runtimeState.LifecycleState})",
            BotCardVisualState.ActiveSession => "State: active session",
            BotCardVisualState.Error => string.IsNullOrWhiteSpace(runtimeState.LastErrorCode)
                ? "State: error"
                : $"State: error ({runtimeState.LastErrorCode})",
            _ => $"State: {runtimeState.LifecycleState}"
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

public sealed record ServerPluginCatalogItem(string Name, string DisplayName, string Version);


public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
