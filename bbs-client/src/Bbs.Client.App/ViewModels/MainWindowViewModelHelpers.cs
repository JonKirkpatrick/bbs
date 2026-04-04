using System;
using System.Collections.Generic;

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
}
