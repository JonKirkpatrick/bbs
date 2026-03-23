using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bbs.Client.App.ViewModels;
using Bbs.Client.App.Views;
using Bbs.Client.Core.Logging;
using Bbs.Client.Infrastructure.Identity;
using Bbs.Client.Infrastructure.Logging;
using Bbs.Client.Infrastructure.Storage;

namespace Bbs.Client.App;

public partial class App : Application
{
    private IClientLogger? _logger;
    private SqliteClientStorage? _storage;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _logger = new FileClientLogger();
        _storage = new SqliteClientStorage();
        _storage.InitializeAsync().GetAwaiter().GetResult();
        var schemaVersion = _storage.GetSchemaVersionAsync().GetAwaiter().GetResult();
        var identityBootstrap = new ClientIdentityBootstrapper(_storage);
        var identity = identityBootstrap.EnsureClientIdentityAsync().GetAwaiter().GetResult();

        _logger.Log(LogLevel.Information, "app_start", "BBS client started.");
        _logger.Log(LogLevel.Information, "storage_initialized", "SQLite storage initialized.",
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["db_path"] = _storage.DatabasePath,
                ["schema_version"] = schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        _logger.Log(LogLevel.Information, "client_identity_ready", "Client identity available.",
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["client_id"] = identity.Identity.ClientId,
                ["created"] = identity.Created.ToString()
            });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_logger, _storage)
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
