namespace Bbs.Client.Infrastructure.Personas;

/// <summary>
/// Manages persona (project) creation, loading, duplication, renaming, and deletion.
/// Personas are stored as individual SQLite files in ~/.local/state/bbs-client/personas/
/// </summary>
public sealed class PersonaManager
{
    private readonly string _personasDirectory;

    public PersonaManager(string? customPersonasDirectory = null)
    {
        _personasDirectory = customPersonasDirectory ?? PersonaPaths.GetPersonasDirectory();
    }

    /// <summary>
    /// Ensures the personas directory exists.
    /// </summary>
    public Task EnsureDirectoryExistsAsync()
    {
        if (!Directory.Exists(_personasDirectory))
        {
            Directory.CreateDirectory(_personasDirectory);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lists all available personas sorted by name.
    /// </summary>
    public async Task<IReadOnlyList<PersonaMetadata>> ListAvailableAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDirectoryExistsAsync();

        if (!Directory.Exists(_personasDirectory))
        {
            return Array.Empty<PersonaMetadata>();
        }

        var files = Directory.GetFiles(_personasDirectory, "*.sqlite3")
            .OrderBy(f => Path.GetFileNameWithoutExtension(f))
            .ToList();

        var personas = new List<PersonaMetadata>();
        foreach (var filePath in files)
        {
            var fileInfo = new FileInfo(filePath);
            personas.Add(new PersonaMetadata(
                Name: Path.GetFileName(filePath),
                FilePath: filePath,
                CreatedAt: fileInfo.CreationTime.ToUniversalTime(),
                LastAccessedAt: fileInfo.LastAccessTime.ToUniversalTime()));
        }

        return personas;
    }

    /// <summary>
    /// Creates a new persona with the given name. Returns the file path.
    /// </summary>
    public async Task<string> CreatePersonaAsync(string personaName, CancellationToken cancellationToken = default)
    {
        await EnsureDirectoryExistsAsync();

        var sanitizedName = SanitizePersonaName(personaName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new ArgumentException("Persona name must contain at least one valid character.", nameof(personaName));
        }

        var filePath = Path.Combine(_personasDirectory, $"{sanitizedName}.sqlite3");
        if (File.Exists(filePath))
        {
            throw new InvalidOperationException($"Persona '{sanitizedName}' already exists.");
        }

        // Create an empty file; the SQLite storage layer will initialize the schema.
        File.WriteAllText(filePath, string.Empty);
        return filePath;
    }

    /// <summary>
    /// Duplicates an existing persona.
    /// </summary>
    public async Task<string> DuplicatePersonaAsync(string sourceFilePath, string newPersonaName, CancellationToken cancellationToken = default)
    {
        await EnsureDirectoryExistsAsync();

        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException($"Source persona not found: {sourceFilePath}");
        }

        var sanitizedName = SanitizePersonaName(newPersonaName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new ArgumentException("New persona name must contain at least one valid character.", nameof(newPersonaName));
        }

        var targetFilePath = Path.Combine(_personasDirectory, $"{sanitizedName}.sqlite3");
        if (File.Exists(targetFilePath))
        {
            throw new InvalidOperationException($"Persona '{sanitizedName}' already exists.");
        }

        File.Copy(sourceFilePath, targetFilePath, overwrite: false);
        return targetFilePath;
    }

    /// <summary>
    /// Renames an existing persona.
    /// </summary>
    public Task<string> RenamePersonaAsync(string currentFilePath, string newPersonaName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(currentFilePath))
        {
            throw new FileNotFoundException($"Persona not found: {currentFilePath}");
        }

        var sanitizedName = SanitizePersonaName(newPersonaName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new ArgumentException("New persona name must contain at least one valid character.", nameof(newPersonaName));
        }

        var newFilePath = Path.Combine(_personasDirectory, $"{sanitizedName}.sqlite3");
        if (string.Equals(currentFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(currentFilePath);
        }

        if (File.Exists(newFilePath))
        {
            throw new InvalidOperationException($"Persona '{sanitizedName}' already exists.");
        }

        File.Move(currentFilePath, newFilePath, overwrite: false);
        return Task.FromResult(newFilePath);
    }

    /// <summary>
    /// Deletes a persona file.
    /// </summary>
    public Task DeletePersonaAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Persona not found: {filePath}");
        }

        File.Delete(filePath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the full file path for a persona by name.
    /// </summary>
    public string GetPersonaFilePath(string personaName)
    {
        var sanitizedName = SanitizePersonaName(personaName);
        return Path.Combine(_personasDirectory, $"{sanitizedName}.sqlite3");
    }

    private static string SanitizePersonaName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // Remove path separators and other invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name
            .Where(c => !invalidChars.Contains(c) && c != '/')
            .ToArray())
            .Trim();

        return sanitized;
    }
}
