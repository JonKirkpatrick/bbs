using System;
using Bbs.Client.Core.Domain;
using Xunit;

namespace Bbs.Client.Core.Tests;

public sealed class BotCardVisualStateRulesTests
{
    [Fact]
    public void Resolve_ReturnsRegistered_WhenRuntimeStateMissing()
    {
        var state = BotCardVisualStateRules.Resolve(null);

        Assert.Equal(BotCardVisualState.Registered, state);
    }

    [Fact]
    public void Resolve_ReturnsArmed_WhenBotIsArmedAndIdle()
    {
        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.Idle,
            IsAttached: true,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var state = BotCardVisualStateRules.Resolve(runtime);

        Assert.Equal(BotCardVisualState.Attached, state);
    }

    [Fact]
    public void Resolve_ReturnsActiveSession_WhenLifecycleIsActiveSession()
    {
        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.ActiveSession,
            IsAttached: true,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var state = BotCardVisualStateRules.Resolve(runtime);

        Assert.Equal(BotCardVisualState.ActiveSession, state);
    }

    [Fact]
    public void Resolve_ReturnsError_WhenLifecycleIsError()
    {
        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.Error,
            IsAttached: false,
            LastErrorCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var state = BotCardVisualStateRules.Resolve(runtime);

        Assert.Equal(BotCardVisualState.Error, state);
    }

    [Fact]
    public void Resolve_ReturnsError_WhenLastErrorCodePresent()
    {
        var runtime = new AgentRuntimeState(
            BotId: "bot-1",
            LifecycleState: AgentLifecycleState.Idle,
            IsAttached: true,
            LastErrorCode: "socket_timeout",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var state = BotCardVisualStateRules.Resolve(runtime);

        Assert.Equal(BotCardVisualState.Error, state);
    }
}
