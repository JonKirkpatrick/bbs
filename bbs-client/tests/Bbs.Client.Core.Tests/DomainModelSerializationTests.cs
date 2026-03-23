using System;
using System.Collections.Generic;
using System.Text.Json;
using Bbs.Client.Core.Domain;
using Xunit;

namespace Bbs.Client.Core.Tests;

public sealed class DomainModelSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    [Fact]
    public void BotProfile_RoundTripSerialization_PreservesFields()
    {
        var createdAt = DateTimeOffset.Parse("2026-03-23T00:00:00+00:00");
        var updatedAt = DateTimeOffset.Parse("2026-03-23T01:00:00+00:00");

        var original = BotProfile.Create(
            botId: "bot-1",
            name: "Counter Bot",
            launchPath: "/tmp/bot.py",
            launchArgs: new[] { "--level", "2" },
            metadata: new Dictionary<string, string> { ["lang"] = "python" },
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<BotProfile>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(original.BotId, roundTrip!.BotId);
        Assert.Equal(original.Name, roundTrip.Name);
        Assert.Equal(original.LaunchPath, roundTrip.LaunchPath);
        Assert.Equal(original.LaunchArgs, roundTrip.LaunchArgs);
        Assert.Equal(original.Metadata, roundTrip.Metadata);
        Assert.Equal(original.CreatedAtUtc, roundTrip.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, roundTrip.UpdatedAtUtc);
    }

    [Fact]
    public void KnownServer_RoundTripSerialization_PreservesFields()
    {
        var createdAt = DateTimeOffset.Parse("2026-03-23T00:00:00+00:00");
        var updatedAt = DateTimeOffset.Parse("2026-03-23T02:00:00+00:00");

        var original = KnownServer.Create(
            serverId: "srv-01",
            name: "Lab Server",
            host: "localhost",
            port: 8080,
            useTls: false,
            metadata: new Dictionary<string, string> { ["region"] = "local" },
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<KnownServer>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(original.ServerId, roundTrip!.ServerId);
        Assert.Equal(original.Name, roundTrip.Name);
        Assert.Equal(original.Host, roundTrip.Host);
        Assert.Equal(original.Port, roundTrip.Port);
        Assert.Equal(original.UseTls, roundTrip.UseTls);
        Assert.Equal(original.Metadata, roundTrip.Metadata);
        Assert.Equal(original.CreatedAtUtc, roundTrip.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, roundTrip.UpdatedAtUtc);
    }

    [Fact]
    public void ServerPluginCache_RoundTripSerialization_PreservesPluginList()
    {
        var cachedAt = DateTimeOffset.Parse("2026-03-23T03:00:00+00:00");

        var original = ServerPluginCache.Create(
            serverId: "srv-01",
            plugins: new[]
            {
                new PluginDescriptor("counter", "Counter", "1.0.0"),
                new PluginDescriptor("gridworld", "Gridworld RL", "0.9.1")
            },
            cachedAtUtc: cachedAt);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ServerPluginCache>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(original.ServerId, roundTrip!.ServerId);
        Assert.Equal(original.CachedAtUtc, roundTrip.CachedAtUtc);
        Assert.Equal(original.Plugins.Count, roundTrip.Plugins.Count);
        for (var i = 0; i < original.Plugins.Count; i++)
        {
            Assert.Equal(original.Plugins[i], roundTrip.Plugins[i]);
        }
    }
}
