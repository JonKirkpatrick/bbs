using Bbs.Client.Infrastructure.Paths;

namespace Bbs.Client.Infrastructure.Personas;

/// <summary>
/// Path utilities for persona storage.
/// </summary>
internal static class PersonaPaths
{
    public static string GetPersonasDirectory()
    {
        return Path.Combine(AppPaths.GetStateDirectory(), "personas");
    }
}
