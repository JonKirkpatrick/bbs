using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class ServerCardVisualStateRulesTests
{
    [Fact]
    public void Resolve_ReturnsInactive_WhenMetadataMissing()
    {
        var state = ServerCardVisualStateRules.Resolve(null);

        Assert.Equal(ServerCardVisualState.Inactive, state);
    }

    [Fact]
    public void Resolve_ReturnsLive_WhenProbeStatusReachable()
    {
        var metadata = new Dictionary<string, string>
        {
            ["probe_status"] = "reachable"
        };

        var state = ServerCardVisualStateRules.Resolve(metadata);

        Assert.Equal(ServerCardVisualState.Live, state);
    }

    [Fact]
    public void Resolve_ReturnsInactive_WhenProbeStatusUnreachable()
    {
        var metadata = new Dictionary<string, string>
        {
            ["probe_status"] = "unreachable"
        };

        var state = ServerCardVisualStateRules.Resolve(metadata);

        Assert.Equal(ServerCardVisualState.Inactive, state);
    }
}
