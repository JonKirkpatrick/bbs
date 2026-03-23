using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Bbs.Client.Core.Domain;

public sealed record BotProfile(
    string BotId,
    string Name,
    string LaunchPath,
    IReadOnlyList<string> LaunchArgs,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static BotProfile Create(
        string botId,
        string name,
        string launchPath,
        IEnumerable<string>? launchArgs = null,
        IDictionary<string, string>? metadata = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new BotProfile(
            botId,
            name,
            launchPath,
            new ReadOnlyCollection<string>((launchArgs ?? Array.Empty<string>()).ToList()),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata ?? new Dictionary<string, string>())),
            createdAtUtc ?? now,
            updatedAtUtc ?? now);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BotId))
        {
            errors.Add("bot_id_required");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("bot_name_required");
        }

        if (string.IsNullOrWhiteSpace(LaunchPath))
        {
            errors.Add("launch_path_required");
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
