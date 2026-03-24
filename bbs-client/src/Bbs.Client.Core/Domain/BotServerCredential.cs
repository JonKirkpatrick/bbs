using System;

namespace Bbs.Client.Core.Domain;

public sealed record BotServerCredential(
    string ClientBotId,
    string ServerId,
    string? ServerGlobalId,
    string ServerBotId,
    string ServerBotSecret,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static BotServerCredential Create(
        string clientBotId,
        string serverId,
        string? serverGlobalId,
        string serverBotId,
        string serverBotSecret,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new BotServerCredential(
            clientBotId,
            serverId,
            string.IsNullOrWhiteSpace(serverGlobalId) ? null : serverGlobalId.Trim(),
            serverBotId,
            serverBotSecret,
            createdAtUtc ?? now,
            updatedAtUtc ?? now);
    }
}
