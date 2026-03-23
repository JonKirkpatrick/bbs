using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bbs.Client.App.ViewModels;
using Bbs.Client.App.Views;
using Bbs.Client.Core.Logging;
using Bbs.Client.Infrastructure.Logging;

namespace Bbs.Client.App;

public partial class App : Application
{
    private IClientLogger? _logger;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _logger = new FileClientLogger();
        _logger.Log(LogLevel.Information, "app_start", "BBS client started.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_logger)
            };

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _logger?.Log(LogLevel.Information, "app_exit", "BBS client shutting down.",
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["exit_code"] = e.ApplicationExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }
}
