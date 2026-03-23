using System.Collections.Generic;
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
    }

    public string WindowTitle => "BBS Client Alpha";

    public ICommand EmitSampleLogCommand { get; }

    private void EmitSampleLog()
    {
        _logger.Log(LogLevel.Information, "sample_log", "Sample structured log emitted from UI command.",
            new Dictionary<string, string>
            {
                ["source"] = "main_window",
                ["feature"] = "foundation_shell"
            });
    }
}
