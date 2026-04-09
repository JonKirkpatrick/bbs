using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bbs.Client.Core.Domain;

public static partial class ServerPluginCatalogParser
{
    private const string CatalogNotFoundMessage = "Game catalog payload was not found in server response.";
    private const string CatalogInvalidMessage = "Game catalog payload is invalid.";
    private const string DefaultVersion = "n/a";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParseFromJsonCatalog(string rawJson, out IReadOnlyList<PluginDescriptor> plugins, out string failureReason)
    {
        return TryParseCatalogJson(rawJson, out plugins, out failureReason);
    }

    public static bool TryParseFromDashboardHtml(string dashboardHtml, out IReadOnlyList<PluginDescriptor> plugins, out string failureReason)
    {
        plugins = Array.Empty<PluginDescriptor>();
        failureReason = CatalogNotFoundMessage;

        if (string.IsNullOrWhiteSpace(dashboardHtml))
        {
            return false;
        }

        var match = DashboardCatalogRegex().Match(dashboardHtml);
        if (!match.Success || match.Groups.Count < 2)
        {
            return false;
        }

        var payload = match.Groups[1].Value;
        return TryParseCatalogJson(payload, out plugins, out failureReason);
    }

    private static bool TryParseCatalogJson(string rawJson, out IReadOnlyList<PluginDescriptor> plugins, out string failureReason)
    {
        plugins = Array.Empty<PluginDescriptor>();
        failureReason = CatalogInvalidMessage;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<GameCatalogEntryDto>>(rawJson, JsonOptions);
            if (entries is null)
            {
                return false;
            }

            var descriptors = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => new PluginDescriptor(
                    Name: e.Name!.Trim(),
                    DisplayName: string.IsNullOrWhiteSpace(e.DisplayName) ? e.Name!.Trim() : e.DisplayName.Trim(),
                    Version: DefaultVersion)
                {
                    Metadata = BuildPluginMetadata(e)
                })
                .ToArray();

            plugins = descriptors;
            failureReason = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("const\\s+BBS_GAME_CATALOG\\s*=\\s*(\\[[\\s\\S]*?\\]);", RegexOptions.Compiled)]
    private static partial Regex DashboardCatalogRegex();

    private static IReadOnlyDictionary<string, string> BuildPluginMetadata(GameCatalogEntryDto entry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.ViewerClientEntry))
        {
            metadata["viewer_client_entry"] = entry.ViewerClientEntry.Trim();
        }

        metadata["supports_replay"] = entry.SupportsReplay.ToString().ToLowerInvariant();
        metadata["supports_move_clock"] = entry.SupportsMoveClock.ToString().ToLowerInvariant();
        metadata["supports_handicap"] = entry.SupportsHandicap.ToString().ToLowerInvariant();

        if (entry.Args is { Count: > 0 })
        {
            metadata["args_json"] = JsonSerializer.Serialize(entry.Args, JsonOptions);
            metadata["arg_count"] = entry.Args.Count.ToString();
        }

        return metadata;
    }

    private sealed class GameCatalogEntryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("args")]
        public List<GameArgSpecDto>? Args { get; init; }

        [JsonPropertyName("viewer_client_entry")]
        public string? ViewerClientEntry { get; init; }

        [JsonPropertyName("supports_replay")]
        public bool SupportsReplay { get; init; }

        [JsonPropertyName("supports_move_clock")]
        public bool SupportsMoveClock { get; init; }

        [JsonPropertyName("supports_handicap")]
        public bool SupportsHandicap { get; init; }
    }

    private sealed class GameArgSpecDto
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("input_type")]
        public string? InputType { get; init; }

        [JsonPropertyName("placeholder")]
        public string? Placeholder { get; init; }

        [JsonPropertyName("default_value")]
        public string? DefaultValue { get; init; }

        [JsonPropertyName("required")]
        public bool Required { get; init; }

        [JsonPropertyName("help")]
        public string? Help { get; init; }
    }
}