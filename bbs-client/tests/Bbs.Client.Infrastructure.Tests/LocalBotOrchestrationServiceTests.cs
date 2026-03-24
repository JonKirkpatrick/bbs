using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Orchestration;
using Bbs.Client.Infrastructure.Storage;
using Xunit;

namespace Bbs.Client.Infrastructure.Tests;

public sealed class LocalBotOrchestrationServiceTests
{
    [Fact]
    public async Task ArmBotAsync_WithExistingLaunchPath_SetsIdleArmedState()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();

        var launchPath = CreateTempExecutableStub();
        var profile = BotProfile.Create(
            botId: "bot-arm-ok",
            name: "Bot",
            launchPath: launchPath);

        var service = new LocalBotOrchestrationService(storage);
        var result = await service.ArmBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(result.Succeeded);
        Assert.NotNull(state);
        Assert.True(state!.IsArmed);
        Assert.Equal(AgentLifecycleState.Idle, state.LifecycleState);
        Assert.Null(state.LastErrorCode);
    }

    [Fact]
    public async Task ArmBotAsync_WithMissingLaunchPath_SetsErrorState()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();

        var profile = BotProfile.Create(
            botId: "bot-arm-missing",
            name: "Bot",
            launchPath: "/tmp/does-not-exist-bot.py");

        var service = new LocalBotOrchestrationService(storage);
        var result = await service.ArmBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.False(result.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsArmed);
        Assert.Equal(AgentLifecycleState.Error, state.LifecycleState);
        Assert.Equal("launch_path_missing", state.LastErrorCode);
    }

    [Fact]
    public async Task DisarmBotAsync_SetsStoppedDisarmedState()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();

        var launchPath = CreateTempExecutableStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm",
            name: "Bot",
            launchPath: launchPath);

        var service = new LocalBotOrchestrationService(storage);
        await service.ArmBotAsync(profile);
        var result = await service.DisarmBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(result.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsArmed);
        Assert.Equal(AgentLifecycleState.Stopped, state.LifecycleState);
        Assert.Null(state.LastErrorCode);
    }

    [Fact]
    public async Task DisarmBotAsync_CanBeRetriedWithoutRestart_AfterInitialFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm-retry",
            name: "Retry Bot",
            launchPath: launchPath);

        var failingService = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new InvalidOperationException("stale handle")));
        var failed = await failingService.DisarmBotAsync(profile);

        Assert.False(failed.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, failed.RuntimeState.LifecycleState);
        Assert.Equal("stale_process_handle", failed.RuntimeState.LastErrorCode);

        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();
        var healthyService = new LocalBotOrchestrationService(storage);

        await healthyService.ArmBotAsync(profile);
        var retry = await healthyService.DisarmBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(retry.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsArmed);
        Assert.Equal(AgentLifecycleState.Stopped, state.LifecycleState);
    }

    [Fact]
    public async Task ArmBotAsync_WhenRuntimePersistThrowsSocketException_ReturnsRecoverableFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var profile = BotProfile.Create(
            botId: "bot-arm-socket-failure",
            name: "Socket Failure Bot",
            launchPath: launchPath);

        var service = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new SocketException((int)SocketError.ConnectionReset)));
        var result = await service.ArmBotAsync(profile);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, result.RuntimeState.LifecycleState);
        Assert.False(result.RuntimeState.IsArmed);
        Assert.Equal("socket_connectionreset", result.RuntimeState.LastErrorCode);
        Assert.Contains("retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisarmBotAsync_WhenRuntimePersistThrowsStaleHandle_ReturnsRecoverableFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm-stale-failure",
            name: "Stale Handle Bot",
            launchPath: launchPath);

        var service = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new InvalidOperationException("stale process")));
        var result = await service.DisarmBotAsync(profile);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, result.RuntimeState.LifecycleState);
        Assert.False(result.RuntimeState.IsArmed);
        Assert.Equal("stale_process_handle", result.RuntimeState.LastErrorCode);
        Assert.Contains("retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewTempDatabasePath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, "client.db");
    }

    private static string CreateTempExecutableStub()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var path = Path.Combine(baseDir, "bot.sh");
        File.WriteAllText(path, "#!/bin/sh\necho ok\n");
        return path;
    }

    private sealed class ThrowingRuntimeStateStorage : IClientStorage
    {
        private readonly Exception _exception;

        public ThrowingRuntimeStateStorage(Exception exception)
        {
            _exception = exception;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<ClientIdentity?> GetClientIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult<ClientIdentity?>(null);

        public Task SaveClientIdentityAsync(ClientIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BotProfile>> ListBotProfilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BotProfile>>(Array.Empty<BotProfile>());

        public Task UpsertBotProfileAsync(BotProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BotServerCredential?> GetBotServerCredentialAsync(string clientBotId, string serverId, string? serverGlobalId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<BotServerCredential?>(null);

        public Task UpsertBotServerCredentialAsync(BotServerCredential credential, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<KnownServer>> ListKnownServersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<KnownServer>>(Array.Empty<KnownServer>());

        public Task UpsertKnownServerAsync(KnownServer server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ServerPluginCache?> GetServerPluginCacheAsync(string serverId, CancellationToken cancellationToken = default)
            => Task.FromResult<ServerPluginCache?>(null);

        public Task UpsertServerPluginCacheAsync(ServerPluginCache cache, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AgentRuntimeState?> GetAgentRuntimeStateAsync(string botId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRuntimeState?>(null);

        public Task UpsertAgentRuntimeStateAsync(AgentRuntimeState state, CancellationToken cancellationToken = default)
            => Task.FromException(_exception);
    }
}
