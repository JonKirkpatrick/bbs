using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Infrastructure.Storage;
using Xunit;

namespace Bbs.Client.Infrastructure.Tests;

public sealed class SqliteClientStorageTests
{
    [Fact]
    public async Task InitializeAsync_CreatesDatabaseFile_AndIsIdempotent()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);

        await storage.InitializeAsync();
        await storage.InitializeAsync();

        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task StorageContracts_RoundTripAcrossAllCoreEntities()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();

        var identity = new ClientIdentity("client-1", "Johnny", DateTimeOffset.Parse("2026-03-23T00:00:00+00:00"));
        await storage.SaveClientIdentityAsync(identity);

        var bot = BotProfile.Create(
            botId: "bot-1",
            name: "Counter Bot",
            launchPath: "/opt/bots/counter.py",
            launchArgs: new[] { "--ranked" },
            metadata: new Dictionary<string, string> { ["lang"] = "python" },
            createdAtUtc: DateTimeOffset.Parse("2026-03-23T00:00:00+00:00"),
            updatedAtUtc: DateTimeOffset.Parse("2026-03-23T01:00:00+00:00"));
        await storage.UpsertBotProfileAsync(bot);

        var server = KnownServer.Create(
            serverId: "srv-1",
            name: "Local",
            host: "127.0.0.1",
            port: 8080,
            metadata: new Dictionary<string, string> { ["region"] = "local" },
            createdAtUtc: DateTimeOffset.Parse("2026-03-23T00:00:00+00:00"),
            updatedAtUtc: DateTimeOffset.Parse("2026-03-23T01:00:00+00:00"));
        await storage.UpsertKnownServerAsync(server);

        var cache = ServerPluginCache.Create(
            serverId: "srv-1",
            plugins: new[] { new PluginDescriptor("counter", "Counter", "1.0.0") },
            cachedAtUtc: DateTimeOffset.Parse("2026-03-23T02:00:00+00:00"));
        await storage.UpsertServerPluginCacheAsync(cache);

        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.Idle,
            IsArmed: true,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.Parse("2026-03-23T03:00:00+00:00"));
        await storage.UpsertAgentRuntimeStateAsync(runtime);

        var loadedIdentity = await storage.GetClientIdentityAsync();
        var loadedBots = await storage.ListBotProfilesAsync();
        var loadedServers = await storage.ListKnownServersAsync();
        var loadedCache = await storage.GetServerPluginCacheAsync("srv-1");
        var loadedRuntime = await storage.GetAgentRuntimeStateAsync("bot-1");

        Assert.NotNull(loadedIdentity);
        Assert.Equal(identity.ClientId, loadedIdentity!.ClientId);
        Assert.Equal(identity.DisplayName, loadedIdentity.DisplayName);

        Assert.Single(loadedBots);
        var loadedBot = loadedBots.Single();
        Assert.Equal(bot.BotId, loadedBot.BotId);
        Assert.Equal(bot.Name, loadedBot.Name);
        Assert.Equal(bot.LaunchPath, loadedBot.LaunchPath);
        Assert.Equal(bot.LaunchArgs, loadedBot.LaunchArgs);

        Assert.Single(loadedServers);
        var loadedServer = loadedServers.Single();
        Assert.Equal(server.ServerId, loadedServer.ServerId);
        Assert.Equal(server.Host, loadedServer.Host);
        Assert.Equal(server.Port, loadedServer.Port);

        Assert.NotNull(loadedCache);
        Assert.Equal(cache.ServerId, loadedCache!.ServerId);
        Assert.Single(loadedCache.Plugins);
        Assert.Equal("counter", loadedCache.Plugins[0].Name);

        Assert.NotNull(loadedRuntime);
        Assert.Equal(runtime.BotId, loadedRuntime!.BotId);
        Assert.Equal(runtime.LifecycleState, loadedRuntime.LifecycleState);
        Assert.True(loadedRuntime.IsArmed);
    }

    private static string NewTempDatabasePath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, "client.db");
    }
}
