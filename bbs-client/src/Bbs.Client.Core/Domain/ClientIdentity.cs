using System;
using System.Collections.Generic;

namespace Bbs.Client.Core.Domain;

public sealed record ClientIdentity(
    string ClientId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            errors.Add("client_id_required");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("display_name_required");
        }

        if (CreatedAtUtc == default)
        {
            errors.Add("created_at_utc_required");
        }

        return errors;
    }
}
