using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class BotPortWelcomeHintParserTests
{
    [Fact]
    public void TryExtractDashboardPort_ReturnsTrue_WhenWelcomeContainsStringDashboardPort()
    {
        const string payload = """
        {
          "status": "ok",
          "type": "welcome",
          "payload": {
            "message": "Welcome",
            "dashboard_port": "3000"
          }
        }
        """;

        var parsed = BotPortWelcomeHintParser.TryExtractDashboardPort(payload, out var port);

        Assert.True(parsed);
        Assert.Equal(3000, port);
    }

    [Fact]
    public void TryExtractDashboardPort_ReturnsTrue_WhenWelcomeContainsNumericDashboardPort()
    {
        const string payload = """
        {
          "status": "ok",
          "type": "welcome",
          "payload": {
            "dashboard_port": 3000
          }
        }
        """;

        var parsed = BotPortWelcomeHintParser.TryExtractDashboardPort(payload, out var port);

        Assert.True(parsed);
        Assert.Equal(3000, port);
    }

    [Fact]
    public void TryExtractDashboardPort_ReturnsFalse_WhenTypeIsNotWelcome()
    {
        const string payload = """
        {
          "type": "event",
          "payload": {
            "dashboard_port": "3000"
          }
        }
        """;

        var parsed = BotPortWelcomeHintParser.TryExtractDashboardPort(payload, out _);

        Assert.False(parsed);
    }
}