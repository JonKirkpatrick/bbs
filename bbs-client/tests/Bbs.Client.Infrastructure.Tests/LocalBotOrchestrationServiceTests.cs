using System.Net.Sockets;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Orchestration;
using Bbs.Client.Infrastructure.Storage;

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
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-arm-ok",
            name: "Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var service = new LocalBotOrchestrationService(storage);
        var result = await service.LaunchBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(result.Succeeded);
        Assert.NotNull(state);
        Assert.True(state!.IsAttached);
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
        var result = await service.LaunchBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.False(result.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsAttached);
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
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm",
            name: "Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var service = new LocalBotOrchestrationService(storage);
        await service.LaunchBotAsync(profile);
        var result = await service.StopBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(result.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsAttached);
        Assert.Equal(AgentLifecycleState.Stopped, state.LifecycleState);
        Assert.Null(state.LastErrorCode);
    }

    [Fact]
    public async Task DisarmBotAsync_CanBeRetriedWithoutRestart_AfterInitialFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm-retry",
            name: "Retry Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var failingService = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new InvalidOperationException("stale handle")));
        var failed = await failingService.StopBotAsync(profile);

        Assert.False(failed.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, failed.RuntimeState.LifecycleState);
        Assert.Equal("stale_process_handle", failed.RuntimeState.LastErrorCode);

        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();
        var healthyService = new LocalBotOrchestrationService(storage);

        await healthyService.LaunchBotAsync(profile);
        var retry = await healthyService.StopBotAsync(profile);
        var state = await storage.GetAgentRuntimeStateAsync(profile.BotId);

        Assert.True(retry.Succeeded);
        Assert.NotNull(state);
        Assert.False(state!.IsAttached);
        Assert.Equal(AgentLifecycleState.Stopped, state.LifecycleState);
    }

    [Fact]
    public async Task ArmBotAsync_WhenRuntimePersistThrowsSocketException_ReturnsRecoverableFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-arm-socket-failure",
            name: "Socket Failure Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var service = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new SocketException((int)SocketError.ConnectionReset)));
        var result = await service.LaunchBotAsync(profile);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, result.RuntimeState.LifecycleState);
        Assert.False(result.RuntimeState.IsAttached);
        Assert.Equal("socket_connectionreset", result.RuntimeState.LastErrorCode);
        Assert.Contains("retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisarmBotAsync_WhenRuntimePersistThrowsStaleHandle_ReturnsRecoverableFailure()
    {
        var launchPath = CreateTempExecutableStub();
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-disarm-stale-failure",
            name: "Stale Handle Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var service = new LocalBotOrchestrationService(new ThrowingRuntimeStateStorage(new InvalidOperationException("stale process")));
        var result = await service.StopBotAsync(profile);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentLifecycleState.Error, result.RuntimeState.LifecycleState);
        Assert.False(result.RuntimeState.IsAttached);
        Assert.Equal("stale_process_handle", result.RuntimeState.LastErrorCode);
        Assert.Contains("retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArmBotAsync_InjectsSocketArgumentIntoBotProcess()
    {
        var dbPath = NewTempDatabasePath();
        var storage = new SqliteClientStorage(dbPath);
        await storage.InitializeAsync();

        var markerPath = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"), "bot-args.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);

        var launchPath = CreateTempSocketExpectingBotStub(markerPath);
        var agentPath = CreateTempAgentStub();
        var profile = BotProfile.Create(
            botId: "bot-socket-injected",
            name: "Socket Injected Bot",
            launchPath: launchPath,
            metadata: new Dictionary<string, string>
            {
                ["agent.launch_path"] = agentPath
            });

        var service = new LocalBotOrchestrationService(storage);
        var armResult = await service.LaunchBotAsync(profile);
        Assert.True(armResult.Succeeded);

        await Task.Delay(300);
        var argsLine = File.Exists(markerPath) ? File.ReadAllText(markerPath) : string.Empty;
        Assert.Contains("--socket", argsLine, StringComparison.Ordinal);
        Assert.Contains("bbs-agent-bot-socket-injected.sock", argsLine, StringComparison.Ordinal);

        await service.StopBotAsync(profile);
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
        File.WriteAllText(path, "#!/bin/sh\ntrap 'exit 0' TERM INT\nwhile true; do sleep 1; done\n");
        return path;
    }

    private static string CreateTempSocketExpectingBotStub(string markerPath)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var path = Path.Combine(baseDir, "bot-capture.sh");
        var escapedMarkerPath = markerPath.Replace("\"", "\\\"");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "echo \"$@\" > \"" + escapedMarkerPath + "\"\n" +
            "trap 'exit 0' TERM INT\n" +
            "while true; do sleep 1; done\n");
        return path;
    }

    private static string CreateTempAgentStub()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "bbs-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var path = Path.Combine(baseDir, "agent.sh");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "socket=\"\"\n" +
            "while [ $# -gt 0 ]; do\n" +
            "  if [ \"$1\" = \"--listen\" ] && [ $# -gt 1 ]; then\n" +
            "    socket=\"$2\"\n" +
            "    shift 2\n" +
            "    continue\n" +
            "  fi\n" +
            "  case \"$1\" in\n" +
            "    --listen=*) socket=\"${1#--listen=}\" ; shift ; continue ;;\n" +
            "  esac\n" +
            "  shift\n" +
            "done\n" +
            "if [ -n \"$socket\" ]; then\n" +
            "  case \"$socket\" in\n" +
            "    unix://*) socket=\"${socket#unix://}\" ;;\n" +
            "  esac\n" +
            "  mkdir -p \"$(dirname \"$socket\")\"\n" +
            "  : > \"$socket\"\n" +
            "fi\n" +
            "trap 'exit 0' TERM INT\n" +
            "while true; do sleep 1; done\n");
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
