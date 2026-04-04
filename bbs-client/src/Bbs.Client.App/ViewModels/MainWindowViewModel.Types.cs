using System;
using System.Collections.Generic;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed record ServerMetadataEntryItem(string Key, string Value);

public sealed record ServerPluginCatalogItem(
    string Name,
    string DisplayName,
    string Version,
    IReadOnlyDictionary<string, string>? PluginMetadata = null)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        PluginMetadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RegisterHandshakeResult(
    string SessionId,
    string ServerBotId,
    string ServerBotSecret,
    string OwnerToken,
    string DashboardEndpoint);

public sealed record AgentControlResponse(
    string Type,
    string Id,
    string Message,
    string SessionId,
    string BotId,
    string BotSecret,
    string OwnerToken,
    string DashboardEndpoint,
    string DashboardHost,
    string DashboardPort);

public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2
}
