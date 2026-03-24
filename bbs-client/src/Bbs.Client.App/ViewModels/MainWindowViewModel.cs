using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private const int ServerProbeMaxAttempts = 2;
    private const int ServerProbeRetryDelayMs = 200;
    private const string ProbeStatusMetadataKey = "probe_status";
    private const string ProbeLastCheckedMetadataKey = "probe_last_checked_utc";
    private const string ProbeLastErrorMetadataKey = "probe_last_error";

    private readonly IClientLogger _logger;
    private readonly IClientStorage _storage;
    private readonly IBotOrchestrationService _orchestration;
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
    private string _serverEditorPlugins = string.Empty;
    private string _serverEditorMessage = "Fill out the server form and save.";

    public MainWindowViewModel(IClientLogger logger, IClientStorage storage, IBotOrchestrationService orchestration)
    {
        _logger = logger;
        _storage = storage;
        _orchestration = orchestration;
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

        Bots = new ObservableCollection<BotSummaryItem>();
        Servers = new ObservableCollection<ServerSummaryItem>();

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

        RefreshContextProjection();
        StartStartupServerProbe();
    }

    public string WindowTitle => "BBS Client Alpha";
    public string WorkspaceTitle { get; private set; } = "Context Host Ready";
    public string WorkspaceDescription { get; private set; } = "Select a bot or server to load activity in this center workspace.";

    public string CurrentContextLabel => $"Context: {_currentContext}";
    public bool ShowBotEditor => _currentContext != WorkspaceContext.ServerDetails;
    public bool ShowServerEditor => _currentContext == WorkspaceContext.ServerDetails;

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

    public string ServerEditorPlugins
    {
        get => _serverEditorPlugins;
        set
        {
            if (_serverEditorPlugins == value)
            {
                return;
            }

            _serverEditorPlugins = value;
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
            ((RelayCommand)SetServerContextCommand).RaiseCanExecuteChanged();
            if (value is not null)
            {
                _currentContext = WorkspaceContext.ServerDetails;
                PopulateServerEditor(value);
                RefreshContextProjection();
            }
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
        ServerEditorPlugins = string.Empty;
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

        var profile = SelectedBot.ToProfile();
        var result = _orchestration.ArmBotAsync(profile).GetAwaiter().GetResult();
        LoadBotsFromStorage();
        SelectedBot = FindBotById(profile.BotId);
        BotEditorMessage = result.Message;
    }

    private void DisarmSelectedBot()
    {
        if (SelectedBot is null)
        {
            return;
        }

        var profile = SelectedBot.ToProfile();
        var result = _orchestration.DisarmBotAsync(profile).GetAwaiter().GetResult();
        LoadBotsFromStorage();
        SelectedBot = FindBotById(profile.BotId);
        BotEditorMessage = result.Message;
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
        ServerEditorPlugins = FormatPluginCatalog(server.CachedPlugins);
        ServerEditorMessage = $"Editing known server: {server.Name}";
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

        var plugins = ParsePluginCatalog(ServerEditorPlugins, out var pluginErrors);
        if (pluginErrors.Count > 0)
        {
            ServerEditorMessage = $"Cannot save plugin cache: {string.Join(", ", pluginErrors)}";
            return;
        }

        var cache = ServerPluginCache.Create(serverId, plugins, DateTimeOffset.UtcNow);

        _storage.UpsertKnownServerAsync(server).GetAwaiter().GetResult();
        _storage.UpsertServerPluginCacheAsync(cache).GetAwaiter().GetResult();
        LoadServersFromStorage();
        SelectedServer = FindServerById(serverId);
        ServerEditorMessage = $"Saved known server: {server.Name}";
        _logger.Log(LogLevel.Information, "known_server_saved", "Known server and plugin cache persisted.",
            new Dictionary<string, string>
            {
                ["server_id"] = server.ServerId,
                ["name"] = server.Name,
                ["plugin_count"] = plugins.Count.ToString()
            });
    }

    private void StartStartupServerProbe()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeKnownServersAtStartupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, "startup_server_probe_failed", "Startup server probe failed unexpectedly.",
                    new Dictionary<string, string>
                    {
                        ["error"] = ex.GetType().Name
                    });
            }
        });
    }

    private async Task ProbeKnownServersAtStartupAsync(CancellationToken cancellationToken)
    {
        var knownServers = await _storage.ListKnownServersAsync(cancellationToken);
        if (knownServers.Count == 0)
        {
            return;
        }

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
            }
            else
            {
                metadata[ProbeLastErrorMetadataKey] = probeResult.ErrorCode;
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
        });
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

    private static string FormatPluginCatalog(IReadOnlyList<PluginDescriptor> plugins)
    {
        if (plugins.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>(plugins.Count);
        foreach (var plugin in plugins)
        {
            lines.Add($"{plugin.Name}|{plugin.DisplayName}|{plugin.Version}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<PluginDescriptor> ParsePluginCatalog(string raw, out IReadOnlyList<string> errors)
    {
        var plugins = new List<PluginDescriptor>();
        var parseErrors = new List<string>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors = parseErrors;
            return plugins;
        }

        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
            {
                parseErrors.Add($"plugin_line_{i + 1}_format_invalid");
                continue;
            }

            var descriptor = new PluginDescriptor(parts[0], parts[1], parts[2]);
            var descriptorErrors = descriptor.Validate();
            foreach (var descriptorError in descriptorErrors)
            {
                parseErrors.Add($"plugin_line_{i + 1}_{descriptorError}");
            }

            if (descriptorErrors.Count == 0)
            {
                plugins.Add(descriptor);
            }
        }

        errors = parseErrors;
        return plugins;
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
    public int PluginCount => CachedPlugins.Count;

    public static ServerSummaryItem FromKnownServer(KnownServer server, ServerPluginCache? cache)
    {
        var scheme = server.UseTls ? "https" : "http";
        var endpoint = $"{scheme}://{server.Host}:{server.Port}";
        var plugins = cache?.Plugins ?? Array.Empty<PluginDescriptor>();
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
            Status = status
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


public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
