using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Storage;

public interface IClientStorage
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    Task<ClientIdentity?> GetClientIdentityAsync(CancellationToken cancellationToken = default);
    Task SaveClientIdentityAsync(ClientIdentity identity, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BotProfile>> ListBotProfilesAsync(CancellationToken cancellationToken = default);
    Task UpsertBotProfileAsync(BotProfile profile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnownServer>> ListKnownServersAsync(CancellationToken cancellationToken = default);
    Task UpsertKnownServerAsync(KnownServer server, CancellationToken cancellationToken = default);

    Task<ServerPluginCache?> GetServerPluginCacheAsync(string serverId, CancellationToken cancellationToken = default);
    Task UpsertServerPluginCacheAsync(ServerPluginCache cache, CancellationToken cancellationToken = default);

    Task<AgentRuntimeState?> GetAgentRuntimeStateAsync(string botId, CancellationToken cancellationToken = default);
    Task UpsertAgentRuntimeStateAsync(AgentRuntimeState state, CancellationToken cancellationToken = default);
}
