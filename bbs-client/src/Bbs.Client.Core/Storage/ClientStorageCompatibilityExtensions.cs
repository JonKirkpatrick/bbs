using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Storage;

public static class ClientStorageCompatibilityExtensions
{
    public static Task<BotServerCredential?> GetBotServerCredentialAsync(this IClientStorage storage, string clientBotId, string serverId, string? serverGlobalId = null, CancellationToken cancellationToken = default)
    {
        _ = storage;
        _ = clientBotId;
        _ = serverId;
        _ = serverGlobalId;
        _ = cancellationToken;
        return Task.FromResult<BotServerCredential?>(null);
    }

    public static Task UpsertBotServerCredentialAsync(this IClientStorage storage, BotServerCredential credential, CancellationToken cancellationToken = default)
    {
        _ = storage;
        _ = credential;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
