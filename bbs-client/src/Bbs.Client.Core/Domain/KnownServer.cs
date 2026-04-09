using System.Collections.ObjectModel;

namespace Bbs.Client.Core.Domain;

public sealed record KnownServer(
    string ServerId,
    string Name,
    string Host,
    int Port,
    bool UseTls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static KnownServer Create(
        string serverId,
        string name,
        string host,
        int port,
        bool useTls = false,
        IDictionary<string, string>? metadata = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new KnownServer(
            serverId,
            name,
            host,
            port,
            useTls,
            createdAtUtc ?? now,
            updatedAtUtc ?? now,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata ?? new Dictionary<string, string>())));
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerId))
        {
            errors.Add("server_id_required");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("server_name_required");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("server_host_required");
        }

        if (Port <= 0 || Port > 65535)
        {
            errors.Add("server_port_invalid");
        }

        if (CreatedAtUtc == default)
        {
            errors.Add("created_at_utc_required");
        }

        if (UpdatedAtUtc == default)
        {
            errors.Add("updated_at_utc_required");
        }

        if (UpdatedAtUtc < CreatedAtUtc)
        {
            errors.Add("updated_before_created");
        }

        return errors;
    }
}
