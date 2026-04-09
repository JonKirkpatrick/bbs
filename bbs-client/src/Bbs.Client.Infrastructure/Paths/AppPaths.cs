
namespace Bbs.Client.Infrastructure.Paths;

internal static class AppPaths
{
    public static string GetStateDirectory()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(stateHome))
        {
            return Path.Combine(stateHome, "bbs-client");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "bbs-client");
    }

    public static string GetLogFilePath()
    {
        return Path.Combine(GetStateDirectory(), "logs", "client.log");
    }
}
