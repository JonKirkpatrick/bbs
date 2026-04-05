using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bbs.Client.App.ViewModels;
using Bbs.Client.App.Views;
using Bbs.Client.Core.Logging;
using Bbs.Client.Infrastructure.Logging;
using Bbs.Client.Infrastructure.Personas;
using System;

namespace Bbs.Client.App;

public partial class App : Application
{
    private IClientLogger? _logger;
    private PersonaManager? _personaManager;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _logger = new FileClientLogger();
        _personaManager = new PersonaManager();

        _logger.Log(LogLevel.Information, "app_start", "BBS client started.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (embeddedViewerAvailable, embeddedViewerMessage) = ProbeEmbeddedViewerAvailability();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    _logger,
                    _personaManager,
                    embeddedViewerAvailable,
                    embeddedViewerMessage)
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

    private static (bool IsAvailable, string Message) ProbeEmbeddedViewerAvailability()
    {
        if (OperatingSystem.IsLinux() && !string.Equals(Environment.GetEnvironmentVariable("BBS_ENABLE_EMBEDDED_WEBVIEW"), "1", StringComparison.Ordinal))
        {
            return (false, "Embedded WebView is disabled by default on Linux. Set BBS_ENABLE_EMBEDDED_WEBVIEW=1 to enable experimental in-app JS rendering.");
        }

        try
        {
            var webViewType = Type.GetType("WebViewControl.WebView, WebViewControl.Avalonia", throwOnError: true)!;
            var version = webViewType.Assembly.GetName().Version?.ToString() ?? "unknown";
            return (true, $"Embedded WebView is ready (WebViewControl.Avalonia {version}).");
        }
        catch (Exception ex)
        {
            return (false, $"Embedded WebView startup probe failed ({ex.GetType().Name}). Fallback mode enabled.");
        }
    }
}
