using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class ServerPluginCatalogParserTests
{
    [Fact]
    public void TryParseFromJsonCatalog_ReturnsPluginDescriptors_WhenPayloadValid()
    {
        const string payload = """
        [
                    {
                        "name": "counter",
                        "display_name": "Counter",
                        "supports_move_clock": true,
                        "supports_handicap": true,
                        "supports_replay": true,
                        "viewer_client_entry": "counter_viewer.js",
                        "args": [
                            {
                                "key": "target",
                                "label": "Target",
                                "input_type": "number",
                                "default_value": "10"
                            }
                        ]
                    },
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
        Assert.Equal("counter_viewer.js", plugins[0].Metadata["viewer_client_entry"]);
        Assert.Equal("true", plugins[0].Metadata["supports_move_clock"]);
        Assert.Equal("true", plugins[0].Metadata["supports_handicap"]);
        Assert.Equal("true", plugins[0].Metadata["supports_replay"]);
        Assert.True(plugins[0].Metadata.ContainsKey("args_json"));
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
        Assert.Equal("false", plugins[0].Metadata["supports_move_clock"]);
        Assert.Equal("false", plugins[0].Metadata["supports_handicap"]);
        Assert.Equal("false", plugins[0].Metadata["supports_replay"]);
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