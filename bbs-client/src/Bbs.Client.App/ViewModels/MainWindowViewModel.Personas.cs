using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Orchestration;
using Bbs.Client.Infrastructure.Personas;
using Bbs.Client.Infrastructure.Storage;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isDeveloperViewEnabled = true;

    public ObservableCollection<ActiveBotSessionItem> ActiveBotSessions { get; } = new();

    public bool HasActiveBotSessions => ActiveBotSessions.Count > 0;

    public bool ShowActiveBotSessionsEmpty => !HasActiveBotSessions;

    public bool IsDeveloperViewEnabled
    {
        get => _isDeveloperViewEnabled;
        set
        {
            if (_isDeveloperViewEnabled == value)
            {
                return;
            }

            _isDeveloperViewEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowVerboseWorkspace));
            OnPropertyChanged(nameof(ShowGraphicsFirstWorkspacePlaceholder));
        }
    }

    public bool ShowVerboseWorkspace => IsDeveloperViewEnabled;

    public bool ShowGraphicsFirstWorkspacePlaceholder => !ShowVerboseWorkspace;

    public MainWindowViewModel(
        IClientLogger logger,
        PersonaManager personaManager,
        bool embeddedViewerAvailable = true,
        string? embeddedViewerMessage = null)
        : this(
            logger,
            CreateUnloadedBootstrapStorage(),
            CreateUnloadedBootstrapOrchestration(logger),
            embeddedViewerAvailable,
            embeddedViewerMessage)
    {
        _personaManager = personaManager;
        _currentPersonaPath = null;
        OnPropertyChanged(nameof(IsPersonaLoaded));
        OnPropertyChanged(nameof(CurrentPersonaPath));
    }

    public bool IsPersonaLoaded => !string.IsNullOrWhiteSpace(_currentPersonaPath);

    public string? CurrentPersonaPath => _currentPersonaPath;

    public async Task CreatePersonaAsync(string personaName)
    {
        EnsurePersonaManager();
        var path = await _personaManager!.CreatePersonaAsync(personaName);
        await LoadPersonaAsync(path);
    }

    public Task LoadPersonaAsync(string filePath)
    {
        return ReplaceRuntimeForPersonaAsync(filePath);
    }

    public void UnloadPersona()
    {
        if (_orchestration is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _currentPersonaPath = null;
        Bots.Clear();
        Servers.Clear();
        SelectedBot = null;
        SelectedServer = null;
        RefreshSelectedServerDetail();
        TriggerServerAccessRefresh();
        RefreshContextProjection();
        OnPropertyChanged(nameof(IsPersonaLoaded));
        OnPropertyChanged(nameof(CurrentPersonaPath));
    }

    public async Task DuplicateCurrentPersonaAsync(string newPersonaName)
    {
        EnsurePersonaManager();
        EnsureCurrentPersonaLoaded();
        var duplicatedPath = await _personaManager!.DuplicatePersonaAsync(_currentPersonaPath!, newPersonaName);
        await ReplaceRuntimeForPersonaAsync(duplicatedPath);
    }

    public async Task RenameCurrentPersonaAsync(string newPersonaName)
    {
        EnsurePersonaManager();
        EnsureCurrentPersonaLoaded();
        _currentPersonaPath = await _personaManager!.RenamePersonaAsync(_currentPersonaPath!, newPersonaName);
        OnPropertyChanged(nameof(CurrentPersonaPath));
        OnPropertyChanged(nameof(IsPersonaLoaded));
    }

    public async Task DeleteCurrentPersonaAsync()
    {
        EnsurePersonaManager();
        EnsureCurrentPersonaLoaded();

        var toDelete = _currentPersonaPath!;
        UnloadPersona();
        await _personaManager!.DeletePersonaAsync(toDelete);
    }

    private static IClientStorage CreateUnloadedBootstrapStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbs-client-bootstrap-{Guid.NewGuid():N}.sqlite3");
        var storage = new SqliteClientStorage(path);
        storage.InitializeAsync().GetAwaiter().GetResult();
        return storage;
    }

    private static IBotOrchestrationService CreateUnloadedBootstrapOrchestration(IClientLogger logger)
    {
        var storage = CreateUnloadedBootstrapStorage();
        return new LocalBotOrchestrationService(storage, logger);
    }

    private async Task ReplaceRuntimeForPersonaAsync(string filePath)
    {
        var storage = new SqliteClientStorage(filePath);
        await storage.InitializeAsync();

        if (_orchestration is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _storage = storage;
        _orchestration = new LocalBotOrchestrationService(storage, _logger);
        _currentPersonaPath = filePath;

        Bots.Clear();
        Servers.Clear();
        LoadBotsFromStorage();
        LoadServersFromStorage();

        SelectedBot = Bots.FirstOrDefault();
        SelectedServer = Servers.FirstOrDefault();
        RefreshSelectedServerDetail();
        TriggerServerAccessRefresh();
        RefreshContextProjection();
        OnPropertyChanged(nameof(IsPersonaLoaded));
        OnPropertyChanged(nameof(CurrentPersonaPath));
    }

    private void EnsurePersonaManager()
    {
        if (_personaManager is null)
        {
            throw new InvalidOperationException("Persona manager is not configured.");
        }
    }

    private void EnsureCurrentPersonaLoaded()
    {
        if (string.IsNullOrWhiteSpace(_currentPersonaPath))
        {
            throw new InvalidOperationException("No persona is currently loaded.");
        }
    }
}
