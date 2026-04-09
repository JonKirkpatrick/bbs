using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class DomainModelValidationTests
{
    [Fact]
    public void ClientIdentity_Validate_ReturnsErrorsForMissingFields()
    {
        var identity = new ClientIdentity("", "", default);

        var errors = identity.Validate();

        Assert.Contains("client_id_required", errors);
        Assert.Contains("display_name_required", errors);
        Assert.Contains("created_at_utc_required", errors);
    }

    [Fact]
    public void BotProfile_Validate_ReturnsEmptyForValidProfile()
    {
        var profile = BotProfile.Create(
            botId: "bot-1",
            name: "Counter Bot",
            launchPath: "/opt/bots/counter.py",
            launchArgs: new[] { "--mode", "ranked" },
            metadata: new Dictionary<string, string> { ["lang"] = "python" });

        var errors = profile.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void KnownServer_Validate_ReturnsPortErrorWhenOutOfRange()
    {
        var server = KnownServer.Create(
            serverId: "server-1",
            name: "Localhost",
            host: "127.0.0.1",
            port: 70000);

        var errors = server.Validate();

        Assert.Contains("server_port_invalid", errors);
    }

    [Fact]
    public void ServerPluginCache_Validate_PropagatesPluginErrors()
    {
        var cache = ServerPluginCache.Create(
            serverId: "server-1",
            plugins: new[] { new PluginDescriptor("", "", "") });

        var errors = cache.Validate();

        Assert.Contains("plugin_0_name_required", errors);
        Assert.Contains("plugin_0_display_name_required", errors);
        Assert.Contains("plugin_0_version_required", errors);
    }

    [Fact]
    public void AgentRuntimeState_Validate_ReturnsErrorForMissingBotId()
    {
        var state = new AgentRuntimeState("", AgentLifecycleState.Idle, IsAttached: true, LastErrorCode: null, UpdatedAtUtc: DateTimeOffset.UtcNow);

        var errors = state.Validate();

        Assert.Contains("bot_id_required", errors);
    }
}
