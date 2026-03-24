using System;
using System.Collections.Generic;

namespace Bbs.Client.Core.Domain;

public enum ServerCardVisualState
{
    Inactive = 0,
    Live = 1
}

public static class ServerCardVisualStateRules
{
    public static ServerCardVisualState Resolve(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return ServerCardVisualState.Inactive;
        }

        if (metadata.TryGetValue("probe_status", out var probeStatus) &&
            string.Equals(probeStatus, "reachable", StringComparison.OrdinalIgnoreCase))
        {
            return ServerCardVisualState.Live;
        }

        return ServerCardVisualState.Inactive;
    }
}
