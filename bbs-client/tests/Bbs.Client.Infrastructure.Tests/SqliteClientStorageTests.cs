using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Infrastructure.Identity;
using Bbs.Client.Infrastructure.Storage;
using Xunit;

namespace Bbs.Client.Infrastructure.Tests;

public sealed class SqliteClientStorageTests
{
    [Fact]
    public async Task InitializeAsync_CreatesDatabaseFile_IsIdempotent_AndSetsSchemaVersion()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);

        await storage.InitializeAsync();
        await storage.InitializeAsync();
        var schemaVersion = await storage.GetSchemaVersionAsync();

        Assert.True(File.Exists(dbPath));
        Assert.Equal(1, schemaVersion);
    }

    [Fact]
    public async Task InitializeAsync_UpgradesLegacyDatabaseWithoutSchemaVersion()
    {
        var dbPath = NewTempDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await CreateLegacyDatabaseAsync(dbPath);

        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();
        var schemaVersion = await storage.GetSchemaVersionAsync();

        Assert.Equal(1, schemaVersion);
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

    [Fact]
    public async Task ClientIdentityBootstrapper_CreatesOnFirstLaunch_AndPersistsAcrossRestart()
    {
        var dbPath = NewTempDatabasePath();

        var storage1 = new SqliteClientStorage(dbPath);
        await storage1.InitializeAsync();
        var bootstrapper1 = new ClientIdentityBootstrapper(storage1);
        var first = await bootstrapper1.EnsureClientIdentityAsync();

        Assert.True(first.Created);

        var storage2 = new SqliteClientStorage(dbPath);
        await storage2.InitializeAsync();
        var bootstrapper2 = new ClientIdentityBootstrapper(storage2);
        var second = await bootstrapper2.EnsureClientIdentityAsync();

        Assert.False(second.Created);
        Assert.Equal(first.Identity.ClientId, second.Identity.ClientId);
        Assert.Equal(first.Identity.DisplayName, second.Identity.DisplayName);
        Assert.Equal(first.Identity.CreatedAtUtc, second.Identity.CreatedAtUtc);
    }

    [Fact]
    public async Task BotProfiles_PersistAcrossStorageInstances()
    {
        var dbPath = NewTempDatabasePath();

        var storage1 = new SqliteClientStorage(dbPath);
        await storage1.InitializeAsync();
        var bot = BotProfile.Create(
            botId: "bot-persist-1",
            name: "Persistent Bot",
            launchPath: "/opt/bots/persistent.py",
            launchArgs: new[] { "--ranked" },
            metadata: new Dictionary<string, string> { ["lang"] = "python" });
        await storage1.UpsertBotProfileAsync(bot);

        var storage2 = new SqliteClientStorage(dbPath);
        await storage2.InitializeAsync();
        var loaded = await storage2.ListBotProfilesAsync();

        Assert.Contains(loaded, b => b.BotId == "bot-persist-1" && b.Name == "Persistent Bot");
    }

    [Fact]
    public async Task KnownServers_AndPluginCache_PersistAcrossStorageInstances()
    {
        var dbPath = NewTempDatabasePath();

        var storage1 = new SqliteClientStorage(dbPath);
        await storage1.InitializeAsync();

        var server = KnownServer.Create(
            serverId: "server-persist-1",
            name: "Persistent Stadium",
            host: "127.0.0.1",
            port: 8080,
            useTls: false,
            metadata: new Dictionary<string, string> { ["region"] = "local" });

        var cache = ServerPluginCache.Create(
            serverId: "server-persist-1",
            plugins: new[]
            {
                new PluginDescriptor("counter", "Counter", "1.0.0"),
                new PluginDescriptor("viewer", "Viewer", "1.1.0")
            });

        await storage1.UpsertKnownServerAsync(server);
        await storage1.UpsertServerPluginCacheAsync(cache);

        var storage2 = new SqliteClientStorage(dbPath);
        await storage2.InitializeAsync();

        var servers = await storage2.ListKnownServersAsync();
        var loadedServer = Assert.Single(servers);
        Assert.Equal("server-persist-1", loadedServer.ServerId);
        Assert.Equal("Persistent Stadium", loadedServer.Name);
        Assert.Equal("127.0.0.1", loadedServer.Host);
        Assert.Equal(8080, loadedServer.Port);

        var loadedCache = await storage2.GetServerPluginCacheAsync("server-persist-1");
        Assert.NotNull(loadedCache);
        Assert.Equal("server-persist-1", loadedCache!.ServerId);
        Assert.Equal(2, loadedCache.Plugins.Count);
        Assert.Equal("counter", loadedCache.Plugins[0].Name);
        Assert.Equal("viewer", loadedCache.Plugins[1].Name);
    }

    [Fact]
    public async Task KnownServer_ProbeMetadataUpdate_PersistsAcrossStorageInstances()
    {
        var dbPath = NewTempDatabasePath();

        var storage1 = new SqliteClientStorage(dbPath);
        await storage1.InitializeAsync();

        var initialServer = KnownServer.Create(
            serverId: "server-probe-1",
            name: "Probe Stadium",
            host: "127.0.0.1",
            port: 8081,
            useTls: false,
            metadata: new Dictionary<string, string>());
        await storage1.UpsertKnownServerAsync(initialServer);

        var updatedServer = KnownServer.Create(
            serverId: initialServer.ServerId,
            name: initialServer.Name,
            host: initialServer.Host,
            port: initialServer.Port,
            useTls: initialServer.UseTls,
            metadata: new Dictionary<string, string>
            {
                ["probe_status"] = "unreachable",
                ["probe_last_error"] = "timeout",
                ["probe_last_checked_utc"] = DateTimeOffset.UtcNow.ToString("O")
            },
            createdAtUtc: initialServer.CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);
        await storage1.UpsertKnownServerAsync(updatedServer);

        var storage2 = new SqliteClientStorage(dbPath);
        await storage2.InitializeAsync();

        var loadedServers = await storage2.ListKnownServersAsync();
        var loadedServer = Assert.Single(loadedServers);
        Assert.Equal("unreachable", loadedServer.Metadata["probe_status"]);
        Assert.Equal("timeout", loadedServer.Metadata["probe_last_error"]);
        Assert.True(loadedServer.Metadata.ContainsKey("probe_last_checked_utc"));
    }

    private static async Task CreateLegacyDatabaseAsync(string dbPath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS client_identity (
                client_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string NewTempDatabasePath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, "client.db");
    }
}
