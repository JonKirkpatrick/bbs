namespace Bbs.Client.Infrastructure.Personas;

/// <summary>
/// Metadata about a stored persona (project).
/// </summary>
public sealed record PersonaMetadata(
    string Name,
    string FilePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt)
{
    /// <summary>
    /// Returns the display name (derived from filename without extension).
    /// </summary>
    public string DisplayName => Path.GetFileNameWithoutExtension(Name);
}
