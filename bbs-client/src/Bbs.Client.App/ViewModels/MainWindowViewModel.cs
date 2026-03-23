using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Bbs.Client.Core.Logging;

namespace Bbs.Client.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IClientLogger _logger;

    public MainWindowViewModel(IClientLogger logger)
    {
        _logger = logger;
        EmitSampleLogCommand = new RelayCommand(EmitSampleLog);

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
    }

    public string WindowTitle => "BBS Client Alpha";
    public string WorkspaceTitle => "Context Host Ready";
    public string WorkspaceDescription => "Select a bot or server to load activity in this center workspace.";

    public IReadOnlyList<BotSummaryItem> Bots { get; }
    public IReadOnlyList<ServerSummaryItem> Servers { get; }

    public ICommand EmitSampleLogCommand { get; }

    private void EmitSampleLog()
    {
        _logger.Log(LogLevel.Information, "workspace_event", "Unified workspace shell action invoked.",
            new Dictionary<string, string>
            {
                ["source"] = "main_window",
                ["feature"] = "unified_workspace_shell"
            });
    }
}

public sealed record BotSummaryItem(string Name, string Summary, string Status);

public sealed record ServerSummaryItem(string Name, string Endpoint, string Status);
