using System.Collections.ObjectModel;

namespace Bbs.Client.Core.Domain;

public sealed record ServerPluginCache(
    string ServerId,
    IReadOnlyList<PluginDescriptor> Plugins,
    DateTimeOffset CachedAtUtc)
{
    public static ServerPluginCache Create(string serverId, IEnumerable<PluginDescriptor>? plugins, DateTimeOffset? cachedAtUtc = null)
    {
        return new ServerPluginCache(
            serverId,
            new ReadOnlyCollection<PluginDescriptor>((plugins ?? Array.Empty<PluginDescriptor>()).ToList()),
            cachedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerId))
        {
            errors.Add("server_id_required");
        }

        if (CachedAtUtc == default)
        {
            errors.Add("cached_at_utc_required");
        }

        for (var i = 0; i < Plugins.Count; i++)
        {
            var pluginErrors = Plugins[i].Validate();
            foreach (var pluginError in pluginErrors)
            {
                errors.Add($"plugin_{i}_{pluginError}");
            }
        }

        return errors;
    }
}

public sealed record PluginDescriptor(
    string Name,
    string DisplayName,
    string Version)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("name_required");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("display_name_required");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            errors.Add("version_required");
        }

        foreach (var entry in Metadata)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                errors.Add("metadata_key_required");
                break;
            }

            if (entry.Value is null)
            {
                errors.Add($"metadata_{entry.Key}_value_required");
                break;
            }
        }

        return errors;
    }
}
