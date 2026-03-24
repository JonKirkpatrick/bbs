using System;
using System.Collections.Generic;
using System.Linq;
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
                    Version: DefaultVersion))
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

    private sealed class GameCatalogEntryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }
    }
}