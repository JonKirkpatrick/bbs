using System;
using System.IO;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
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
}
