using System.Text.Json;

namespace Bbs.Client.App.ViewModels;

internal static class MainWindowViewModelHelpers
{
    internal static string MaskToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length <= 8)
        {
            return "********";
        }

        return $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    internal static IReadOnlyList<string> ParseArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static Dictionary<string, string> ParseMetadata(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var pairs = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                result[parts[0]] = parts[1];
            }
        }

        return result;
    }

    internal static string FormatMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in metadata)
        {
            parts.Add($"{item.Key}={item.Value}");
        }

        return string.Join(';', parts);
    }

    internal static string? FirstNonEmptyMetadataValue(IReadOnlyDictionary<string, string> metadata, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    internal static int? ParsePositivePort(IReadOnlyDictionary<string, string> metadata, IEnumerable<string> keys)
    {
        var raw = FirstNonEmptyMetadataValue(metadata, keys);
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        return port;
    }

    internal static string BuildBaseEndpoint(string scheme, string host, int port)
    {
        return $"{scheme}://{host}:{port}";
    }

    internal static bool IsStatusOkObject(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("status", out var statusNode) &&
               string.Equals(statusNode.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetStringPropertyOrEmpty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node))
        {
            return string.Empty;
        }

        return node.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => node.GetString() ?? string.Empty,
            _ => node.ToString()
        };
    }

    internal static int GetIntPropertyOrDefault(JsonElement element, string propertyName, int defaultValue = 0)
    {
        return element.TryGetProperty(propertyName, out var node) && node.TryGetInt32(out var value)
            ? value
            : defaultValue;
    }
}
