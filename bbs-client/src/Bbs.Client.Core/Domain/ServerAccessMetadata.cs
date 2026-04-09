namespace Bbs.Client.Core.Domain;

public sealed record ServerAccessMetadata(
    bool IsValid,
    string OwnerToken,
    string DashboardEndpoint,
    string StatusMessage,
    string Source)
{
    public static ServerAccessMetadata Invalid(string statusMessage)
    {
        return new ServerAccessMetadata(
            IsValid: false,
            OwnerToken: string.Empty,
            DashboardEndpoint: string.Empty,
            StatusMessage: statusMessage,
            Source: "none");
    }
}

public static class ServerAccessMetadataResolver
{
    private static readonly string[] OwnerTokenKeys =
    {
        "server_access.owner_token",
        "owner_token",
        "server.owner_token"
    };

    private static readonly string[] DashboardEndpointKeys =
    {
        "server_access.dashboard_endpoint",
        "dashboard_endpoint",
        "server.dashboard_endpoint"
    };

    private static readonly string[] ServerIdKeys =
    {
        "server_access.server_id",
        "server_id"
    };

    public static ServerAccessMetadata Resolve(BotProfile? botProfile, AgentRuntimeState? runtimeState, string? selectedServerId)
    {
        if (botProfile is null)
        {
            return ServerAccessMetadata.Invalid("No bot selected for server access metadata.");
        }

        if (runtimeState is null || !runtimeState.IsAttached)
        {
            return ServerAccessMetadata.Invalid("Selected bot is not attached.");
        }

        var metadata = botProfile.Metadata;
        var metadataServerId = FirstNonEmpty(metadata, ServerIdKeys);
        if (!string.IsNullOrWhiteSpace(selectedServerId) &&
            !string.IsNullOrWhiteSpace(metadataServerId) &&
            !string.Equals(metadataServerId, selectedServerId, StringComparison.OrdinalIgnoreCase))
        {
            return ServerAccessMetadata.Invalid("Attached bot metadata is for a different server.");
        }

        var ownerToken = FirstNonEmpty(metadata, OwnerTokenKeys);
        if (string.IsNullOrWhiteSpace(ownerToken))
        {
            return ServerAccessMetadata.Invalid("Owner token missing from attached bot metadata.");
        }

        var dashboardEndpoint = FirstNonEmpty(metadata, DashboardEndpointKeys);
        if (string.IsNullOrWhiteSpace(dashboardEndpoint))
        {
            return ServerAccessMetadata.Invalid("Dashboard endpoint missing from attached bot metadata.");
        }

        if (!Uri.TryCreate(dashboardEndpoint, UriKind.Absolute, out _))
        {
            return ServerAccessMetadata.Invalid("Dashboard endpoint is not a valid absolute URL.");
        }

        return new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: ownerToken,
            DashboardEndpoint: dashboardEndpoint,
                StatusMessage: "Server access metadata loaded from attached bot session.",
                Source: "attached-bot-metadata");
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> metadata, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
