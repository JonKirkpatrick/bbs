using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Identity;
using Bbs.Client.Infrastructure.Orchestration;
using Bbs.Client.Infrastructure.Personas;
using Bbs.Client.Infrastructure.Storage;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Manages persona (project) lifecycle including loading, unloading, and switching.
/// </summary>
public sealed class PersonaContextManager
{
    private readonly IClientLogger _logger;
    private readonly PersonaManager _personaManager;

    private string? _currentPersonaPath;
    private IClientStorage? _currentStorage;
    private IBotOrchestrationService? _currentOrchestration;

    public PersonaContextManager(IClientLogger logger, PersonaManager personaManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _personaManager = personaManager ?? throw new ArgumentNullException(nameof(personaManager));
    }

    public bool IsLoaded => _currentPersonaPath != null && _currentStorage != null && _currentOrchestration != null;
    public string? CurrentPersonaPath => _currentPersonaPath;
    public IClientStorage? CurrentStorage => _currentStorage;
    public IBotOrchestrationService? CurrentOrchestration => _currentOrchestration;

    public async Task<string> CreateAndLoadPersonaAsync(string personaName, CancellationToken cancellationToken = default)
    {
        if (IsLoaded)
        {
            await UnloadPersonaAsync(cancellationToken);
        }

        var filePath = await _personaManager.CreatePersonaAsync(personaName, cancellationToken);
        await LoadPersonaAsync(filePath, cancellationToken);
        return filePath;
    }

    public async Task LoadPersonaAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (IsLoaded && string.Equals(_currentPersonaPath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return; // Already loaded
        }

        if (IsLoaded)
        {
            await UnloadPersonaAsync(cancellationToken);
        }

        var storage = new SqliteClientStorage(filePath);
        await storage.InitializeAsync(cancellationToken);
        await storage.ClearTransientRuntimeStateAsync(cancellationToken);

        var schemaVersion = await storage.GetSchemaVersionAsync(cancellationToken);
        var orchestration = new LocalBotOrchestrationService(storage, _logger);
        var identityBootstrapper = new ClientIdentityBootstrapper(storage);
        var identity = await identityBootstrapper.EnsureClientIdentityAsync(cancellationToken);

        _currentPersonaPath = filePath;
        _currentStorage = storage;
        _currentOrchestration = orchestration;

        _logger.Log(LogLevel.Information, "persona_loaded", "Persona loaded successfully.",
            new Dictionary<string, string>
            {
                ["persona_path"] = filePath,
                ["client_id"] = identity.Identity.ClientId,
                ["schema_version"] = schemaVersion.ToString()
            });
    }

    public async Task UnloadPersonaAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            // IBotOrchestrationService doesn't implement IDisposable, so just nullify references
            _currentStorage = null;
            _currentOrchestration = null;
            _currentPersonaPath = null;

            _logger.Log(LogLevel.Information, "persona_unloaded", "Persona unloaded.");
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "persona_unload_error", 
                "Error unloading persona.",
                new Dictionary<string, string> { ["error"] = ex.Message });
        }
    }

    public async Task<string> DuplicatePersonaAsync(string newPersonaName, CancellationToken cancellationToken = default)
    {
        if (!IsLoaded || _currentPersonaPath == null)
        {
            throw new InvalidOperationException("No persona is currently loaded.");
        }

        var newFilePath = await _personaManager.DuplicatePersonaAsync(_currentPersonaPath, newPersonaName, cancellationToken);
        _logger.Log(LogLevel.Information, "persona_duplicated", "Persona duplicated.",
            new Dictionary<string, string>
            {
                ["source_path"] = _currentPersonaPath,
                ["new_path"] = newFilePath
            });
        return newFilePath;
    }

    public async Task<string> RenameCurrentPersonaAsync(string newPersonaName, CancellationToken cancellationToken = default)
    {
        if (!IsLoaded || _currentPersonaPath == null)
        {
            throw new InvalidOperationException("No persona is currently loaded.");
        }

        var oldPath = _currentPersonaPath;
        var newPath = await _personaManager.RenamePersonaAsync(oldPath, newPersonaName, cancellationToken);
        _currentPersonaPath = newPath;

        _logger.Log(LogLevel.Information, "persona_renamed", "Persona renamed.",
            new Dictionary<string, string>
            {
                ["old_path"] = oldPath,
                ["new_path"] = newPath
            });

        return newPath;
    }

    public async Task DeletePersonaAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (IsLoaded && string.Equals(_currentPersonaPath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            await UnloadPersonaAsync(cancellationToken);
        }

        await _personaManager.DeletePersonaAsync(filePath, cancellationToken);
        _logger.Log(LogLevel.Information, "persona_deleted", "Persona deleted.",
            new Dictionary<string, string> { ["path"] = filePath });
    }

    public async Task<IReadOnlyList<PersonaMetadata>> ListAvailablePersonasAsync(CancellationToken cancellationToken = default)
    {
        return await _personaManager.ListAvailableAsync(cancellationToken);
    }
}
