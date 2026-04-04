using System;
using System.Collections.Generic;
using Avalonia.Media;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class ServerSummaryItem
{
    private static readonly IBrush LiveAccentBrush = new SolidColorBrush(Color.Parse("#2b8a3e"));
    private static readonly IBrush LiveBackgroundBrush = new SolidColorBrush(Color.Parse("#e8f8ec"));
    private static readonly IBrush InactiveAccentBrush = new SolidColorBrush(Color.Parse("#6c757d"));
    private static readonly IBrush InactiveBackgroundBrush = new SolidColorBrush(Color.Parse("#f1f3f5"));

    public required string ServerId { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool UseTls { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required IReadOnlyList<PluginDescriptor> CachedPlugins { get; init; }
    public required string Endpoint { get; init; }
    public required string Status { get; init; }
    public required IBrush AccentBrush { get; init; }
    public required IBrush BackgroundBrush { get; init; }
    public required ServerCardVisualState VisualState { get; init; }
    public int PluginCount => CachedPlugins.Count;

    public static ServerSummaryItem FromKnownServer(KnownServer server, ServerPluginCache? cache)
    {
        var scheme = server.UseTls ? "https" : "http";
        var endpoint = $"{scheme}://{server.Host}:{server.Port}";
        var plugins = cache?.Plugins ?? Array.Empty<PluginDescriptor>();
        var visualState = ServerCardVisualStateRules.Resolve(server.Metadata);
        var (accentBrush, backgroundBrush) = ResolveBrushes(visualState);
        var status = BuildProbeAwareStatus(server.Metadata, cache);

        return new ServerSummaryItem
        {
            ServerId = server.ServerId,
            Name = server.Name,
            Host = server.Host,
            Port = server.Port,
            UseTls = server.UseTls,
            CreatedAtUtc = server.CreatedAtUtc,
            Metadata = server.Metadata,
            CachedPlugins = plugins,
            Endpoint = endpoint,
            Status = status,
            AccentBrush = accentBrush,
            BackgroundBrush = backgroundBrush,
            VisualState = visualState
        };
    }

    private static (IBrush AccentBrush, IBrush BackgroundBrush) ResolveBrushes(ServerCardVisualState visualState)
    {
        return visualState switch
        {
            ServerCardVisualState.Live => (LiveAccentBrush, LiveBackgroundBrush),
            _ => (InactiveAccentBrush, InactiveBackgroundBrush)
        };
    }

    private static string BuildProbeAwareStatus(IReadOnlyDictionary<string, string> metadata, ServerPluginCache? cache)
    {
        if (metadata.TryGetValue("probe_status", out var probeStatus))
        {
            if (string.Equals(probeStatus, "reachable", StringComparison.OrdinalIgnoreCase))
            {
                return "Status: reachable";
            }

            if (metadata.TryGetValue("probe_last_error", out var probeError) &&
                !string.IsNullOrWhiteSpace(probeError) &&
                probeError.StartsWith("bot_port_dashboard_", StringComparison.OrdinalIgnoreCase))
            {
                var suggestedPort = probeError["bot_port_dashboard_".Length..];
                if (!string.IsNullOrWhiteSpace(suggestedPort))
                {
                    return $"Status: wrong endpoint (bot port). Use dashboard port {suggestedPort}";
                }
            }

            var errorSuffix = metadata.TryGetValue("probe_last_error", out var errorValue) && !string.IsNullOrWhiteSpace(errorValue)
                ? $" ({errorValue})"
                : string.Empty;
            return $"Status: unreachable{errorSuffix}";
        }

        return cache is null
            ? "Status: pending probe"
            : "Status: pending startup probe";
    }
}
