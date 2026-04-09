using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.Infrastructure.Identity;

public sealed class ClientIdentityBootstrapper
{
    private readonly IClientStorage _storage;

    public ClientIdentityBootstrapper(IClientStorage storage)
    {
        _storage = storage;
    }

    public async Task<ClientIdentityBootstrapResult> EnsureClientIdentityAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _storage.GetClientIdentityAsync(cancellationToken);
        if (existing is not null)
        {
            return new ClientIdentityBootstrapResult(existing, Created: false);
        }

        var clientId = Guid.NewGuid().ToString("N");
        var identity = new ClientIdentity(
            ClientId: clientId,
            DisplayName: $"Client-{clientId[..8]}",
            CreatedAtUtc: DateTimeOffset.UtcNow);

        await _storage.SaveClientIdentityAsync(identity, cancellationToken);

        return new ClientIdentityBootstrapResult(identity, Created: true);
    }
}

public sealed record ClientIdentityBootstrapResult(ClientIdentity Identity, bool Created);
