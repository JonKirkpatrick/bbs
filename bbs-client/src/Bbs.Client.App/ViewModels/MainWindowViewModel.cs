using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;
using Bbs.Client.Core.Logging;

namespace Bbs.Client.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IClientLogger _logger;
    private WorkspaceContext _currentContext;
    private BotSummaryItem? _selectedBot;
    private ServerSummaryItem? _selectedServer;
    private bool _isLeftPanelExpanded = true;
    private bool _isRightPanelExpanded = true;

    public MainWindowViewModel(IClientLogger logger)
    {
        _logger = logger;
        EmitSampleLogCommand = new RelayCommand(EmitSampleLog);
        ToggleLeftPanelCommand = new RelayCommand(ToggleLeftPanel);
        ToggleRightPanelCommand = new RelayCommand(ToggleRightPanel);
        SetHomeContextCommand = new RelayCommand(SetHomeContext);
        SetBotContextCommand = new RelayCommand(SetBotContextFromSelection, () => SelectedBot is not null);
        SetServerContextCommand = new RelayCommand(SetServerContextFromSelection, () => SelectedServer is not null);

        Bots = new ReadOnlyCollection<BotSummaryItem>(new[]
        {
            new BotSummaryItem("Counter Bot", "Python entry: /opt/bots/counter.py", "State: idle"),
            new BotSummaryItem("Gridworld Q Bot", "Python entry: /opt/bots/gridworld_q.py", "State: disarmed")
        });

        Servers = new ReadOnlyCollection<ServerSummaryItem>(new[]
        {
            new ServerSummaryItem("Local Stadium", "127.0.0.1:8080", "Status: reachable"),
            new ServerSummaryItem("Lab Server", "10.0.0.42:8080", "Status: pending probe")
        });

        _currentContext = WorkspaceContext.Home;
        _selectedBot = Bots[0];
        _selectedServer = Servers[0];
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
            OnPropertyChanged(nameof(RightPanelWidth));
            OnPropertyChanged(nameof(RightPanelToggleText));
        }
    }

    public GridLength LeftPanelWidth => IsLeftPanelExpanded ? new GridLength(280) : new GridLength(56);
    public GridLength RightPanelWidth => IsRightPanelExpanded ? new GridLength(280) : new GridLength(56);
    public string LeftPanelToggleText => IsLeftPanelExpanded ? "Collapse Bots" : "Expand Bots";
    public string RightPanelToggleText => IsRightPanelExpanded ? "Collapse Servers" : "Expand Servers";

    public IReadOnlyList<BotSummaryItem> Bots { get; }
    public IReadOnlyList<ServerSummaryItem> Servers { get; }

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
            if (value is not null)
            {
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
                WorkspaceTitle = SelectedBot is null ? "Bot Context" : $"Bot: {SelectedBot.Name}";
                WorkspaceDescription = SelectedBot is null
                    ? "Select a bot card to load bot context."
                    : $"{SelectedBot.Summary} | {SelectedBot.Status}";
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
}

public sealed record BotSummaryItem(string Name, string Summary, string Status);

public sealed record ServerSummaryItem(string Name, string Endpoint, string Status);

public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
