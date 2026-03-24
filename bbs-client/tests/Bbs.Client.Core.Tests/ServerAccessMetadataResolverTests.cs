using System.Collections.Generic;
using Bbs.Client.Core.Domain;
using Xunit;

namespace Bbs.Client.Core.Tests;

public sealed class ServerAccessMetadataResolverTests
{
    [Fact]
    public void Resolve_ReturnsInvalid_WhenBotMissing()
    {
        var result = ServerAccessMetadataResolver.Resolve(
            botProfile: null,
            runtimeState: null,
            selectedServerId: "srv-1");

        Assert.False(result.IsValid);
        Assert.Contains("No bot selected", result.StatusMessage);
    }

    [Fact]
    public void Resolve_ReturnsInvalid_WhenBotNotArmed()
    {
        var profile = BotProfile.Create(
            botId: "bot-1",
            name: "Bot",
            launchPath: "/tmp/bot");

        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.Idle,
            IsArmed: false,
            LastErrorCode: null,
            UpdatedAtUtc: System.DateTimeOffset.UtcNow);

        var result = ServerAccessMetadataResolver.Resolve(profile, runtime, "srv-1");

        Assert.False(result.IsValid);
        Assert.Contains("not armed", result.StatusMessage);
    }

    [Fact]
    public void Resolve_ReturnsValid_WhenMetadataPresentForSelectedServer()
    {
        var profile = BotProfile.Create(
            botId: "bot-1",
            name: "Bot",
            launchPath: "/tmp/bot",
            metadata: new Dictionary<string, string>
            {
                ["server_access.server_id"] = "srv-1",
                ["server_access.owner_token"] = "owner-token-value",
                ["server_access.dashboard_endpoint"] = "https://localhost:8080/dashboard"
            });

        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsArmed: true,
            LastErrorCode: null,
            UpdatedAtUtc: System.DateTimeOffset.UtcNow);

        var result = ServerAccessMetadataResolver.Resolve(profile, runtime, "srv-1");

        Assert.True(result.IsValid);
        Assert.Equal("owner-token-value", result.OwnerToken);
        Assert.Equal("https://localhost:8080/dashboard", result.DashboardEndpoint);
    }

    [Fact]
    public void Resolve_ReturnsInvalid_WhenMetadataServerIdMismatchesSelection()
    {
        var profile = BotProfile.Create(
            botId: "bot-1",
            name: "Bot",
            launchPath: "/tmp/bot",
            metadata: new Dictionary<string, string>
            {
                ["server_access.server_id"] = "srv-2",
                ["server_access.owner_token"] = "owner-token-value",
                ["server_access.dashboard_endpoint"] = "https://localhost:8080/dashboard"
            });

        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsArmed: true,
            LastErrorCode: null,
            UpdatedAtUtc: System.DateTimeOffset.UtcNow);

        var result = ServerAccessMetadataResolver.Resolve(profile, runtime, "srv-1");

        Assert.False(result.IsValid);
        Assert.Contains("different server", result.StatusMessage);
    }

    [Fact]
    public void Resolve_ReturnsInvalid_WhenDashboardUrlInvalid()
    {
        var profile = BotProfile.Create(
            botId: "bot-1",
            name: "Bot",
            launchPath: "/tmp/bot",
            metadata: new Dictionary<string, string>
            {
                ["server_access.owner_token"] = "owner-token-value",
                ["server_access.dashboard_endpoint"] = "not-a-url"
            });

        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsArmed: true,
            LastErrorCode: null,
            UpdatedAtUtc: System.DateTimeOffset.UtcNow);

        var result = ServerAccessMetadataResolver.Resolve(profile, runtime, "srv-1");

        Assert.False(result.IsValid);
        Assert.Contains("valid absolute URL", result.StatusMessage);
    }
}
