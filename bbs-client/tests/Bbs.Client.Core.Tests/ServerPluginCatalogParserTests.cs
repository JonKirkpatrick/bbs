using Bbs.Client.Core.Domain;
using Xunit;

namespace Bbs.Client.Core.Tests;

public sealed class ServerPluginCatalogParserTests
{
    [Fact]
    public void TryParseFromJsonCatalog_ReturnsPluginDescriptors_WhenPayloadValid()
    {
        const string payload = """
        [
          { "name": "counter", "display_name": "Counter" },
          { "name": "gridworld_rl", "display_name": "Gridworld RL" }
        ]
        """;

        var parsed = ServerPluginCatalogParser.TryParseFromJsonCatalog(payload, out var plugins, out var reason);

        Assert.True(parsed);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(2, plugins.Count);
        Assert.Equal("counter", plugins[0].Name);
        Assert.Equal("Counter", plugins[0].DisplayName);
        Assert.Equal("n/a", plugins[0].Version);
    }

    [Fact]
    public void TryParseFromDashboardHtml_ReturnsPluginDescriptors_WhenEmbeddedCatalogExists()
    {
        const string html = """
        <html>
        <body>
        <script>
        const BBS_GAME_CATALOG = [{"name":"counter","display_name":"Counter"}];
        </script>
        </body>
        </html>
        """;

        var parsed = ServerPluginCatalogParser.TryParseFromDashboardHtml(html, out var plugins, out var reason);

        Assert.True(parsed);
        Assert.Equal(string.Empty, reason);
        Assert.Single(plugins);
        Assert.Equal("counter", plugins[0].Name);
        Assert.Equal("Counter", plugins[0].DisplayName);
        Assert.Equal("n/a", plugins[0].Version);
    }

    [Fact]
    public void TryParseFromDashboardHtml_ReturnsFalse_WhenCatalogMissing()
    {
        const string html = "<html><script>const SOME_OTHER_VAR = [];</script></html>";

        var parsed = ServerPluginCatalogParser.TryParseFromDashboardHtml(html, out var plugins, out var reason);

        Assert.False(parsed);
        Assert.Empty(plugins);
        Assert.Contains("not found", reason);
    }
}