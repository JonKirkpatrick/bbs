using System.IO;

namespace Bbs.Client.Infrastructure.Paths;

internal static class StoragePaths
{
    public static string GetDataDirectory()
    {
        return Path.Combine(AppPaths.GetStateDirectory(), "data");
    }

    public static string GetDatabaseFilePath()
    {
        return Path.Combine(GetDataDirectory(), "client.db");
    }
}
