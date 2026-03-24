using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Windows.Input;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
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

        Bots = new ObservableCollection<BotSummaryItem>();

        Servers = new ReadOnlyCollection<ServerSummaryItem>(new[]
        {
            new ServerSummaryItem("Local Stadium", "127.0.0.1:8080", "Status: reachable"),
            new ServerSummaryItem("Lab Server", "10.0.0.42:8080", "Status: pending probe")
        });

        _currentContext = WorkspaceContext.Home;
        LoadBotsFromStorage();
        _selectedBot = Bots.Count > 0 ? Bots[0] : null;
        _selectedServer = Servers[0];
        if (_selectedBot is not null)
        {
            PopulateBotEditor(_selectedBot);
        }
        RefreshContextProjection();
    }

    public string WindowTitle => "BBS Client Alpha";
    public string WorkspaceTitle { get; private set; } = "Context Host Ready";
    public string WorkspaceDescription { get; private set; } = "Select a bot or server to load activity in this center workspace.";

    public string CurrentContextLabel => $"Context: {_currentContext}";

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
    public IReadOnlyList<ServerSummaryItem> Servers { get; }

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
                    : $"{SelectedServer.Endpoint} | {SelectedServer.Status}";
                break;
            default:
                WorkspaceTitle = "Context Host Ready";
                WorkspaceDescription = "Select a bot or server to load activity in this center workspace.";
                break;
        }

        OnPropertyChanged(nameof(CurrentContextLabel));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceDescription));
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

    private void PopulateBotEditor(BotSummaryItem bot)
    {
        BotEditorName = bot.Name;
        BotEditorLaunchPath = bot.LaunchPath;
        BotEditorArgs = string.Join(" ", bot.LaunchArgs);
        BotEditorMetadata = FormatMetadata(bot.Metadata);
        BotEditorMessage = $"Editing bot profile: {bot.Name}";
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

public sealed record ServerSummaryItem(string Name, string Endpoint, string Status);

public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
