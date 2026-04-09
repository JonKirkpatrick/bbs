using System.Text.Json;
using Avalonia.Media;
using Bbs.Client.App.ViewModels;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.Core.Tests;

public sealed class SessionServiceViewModelTests
{
    [Fact]
    public void ResolveServerIdFromAgentTarget_MatchesUsingBotPortMetadata()
    {
        var service = CreateSessionService();
        var servers = new[]
        {
            CreateServerSummaryItem("srv-default", "127.0.0.1", 3000, new Dictionary<string, string>()),
            CreateServerSummaryItem("srv-bot-port", "127.0.0.1", 3001, new Dictionary<string, string>
            {
                ["bot_port"] = "9090"
            })
        };

        var resolved = service.ResolveServerIdFromAgentTarget("127.0.0.1:9090", servers, 8080);

        Assert.Equal("srv-bot-port", resolved);
    }

    [Fact]
    public void ResolveServerIdFromAgentTarget_ReturnsEmptyWhenHostOnlyIsAmbiguous()
    {
        var service = CreateSessionService();
        var servers = new[]
        {
            CreateServerSummaryItem("srv-a", "localhost", 3000, new Dictionary<string, string>()),
            CreateServerSummaryItem("srv-b", "127.0.0.1", 3001, new Dictionary<string, string>())
        };

        var resolved = service.ResolveServerIdFromAgentTarget("localhost", servers, 8080);

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void ResolveServerIdFromAgentTarget_ReturnsEmptyWhenNoServerMatches()
    {
        var service = CreateSessionService();
        var servers = new[]
        {
            CreateServerSummaryItem("srv-a", "192.0.2.10", 3000, new Dictionary<string, string>())
        };

        var resolved = service.ResolveServerIdFromAgentTarget("203.0.113.50:8080", servers, 8080);

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void BuildArenaOptionsForServer_PopulatesForResolvedServerId()
    {
        var service = CreateSessionService();
        var selectedServer = CreateServerSummaryItem(
            "srv-join",
            "localhost",
            3000,
            new Dictionary<string, string>
            {
                ["bot_port"] = "8080"
            });

        var arenas = new[]
        {
            new ServerArenaItem(12, "Counter", "running", "1/2", 7, "http://example/12", "http://example/p12", 800, 600, new RelayCommand(() => { })),
            new ServerArenaItem(18, "Fhourstones", "waiting", "0/2", 0, "http://example/18", "http://example/p18", 800, 600, new RelayCommand(() => { }))
        };

        var resolvedServerId = service.ResolveServerIdFromAgentTarget("127.0.0.1:8080", new[] { selectedServer }, 8080);
        var options = service.BuildArenaOptionsForServer(resolvedServerId, selectedServer, arenas);

        Assert.Equal("srv-join", resolvedServerId);
        Assert.Equal(2, options.Count);
        Assert.Contains(options, option => option.ArenaId == 12 && option.Label == "#12 - Counter");
        Assert.Contains(options, option => option.ArenaId == 18 && option.Label == "#18 - Fhourstones");
    }

    [Fact]
    public void ParseControlEnvelope_AllowsNumericPayloadFields()
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "server_access",
            id = "req-1",
            payload = new
            {
                server = "127.0.0.1:8080",
                session_id = 42,
                bot_id = "bot-123",
                control_token = "ctl",
                owner_token = "own",
                dashboard_endpoint = "http://127.0.0.1:3000",
                dashboard_host = "127.0.0.1",
                dashboard_port = 3000
            }
        });

        var envelope = DeploymentTransportHelpers.ParseControlEnvelope(line);

        Assert.Equal("server_access", envelope.Type);
        Assert.Equal("req-1", envelope.Id);
        Assert.Equal("127.0.0.1:8080", envelope.Server);
        Assert.Equal("42", envelope.SessionId);
        Assert.Equal("bot-123", envelope.BotId);
        Assert.Equal("3000", envelope.DashboardPort);
    }

    private static SessionServiceViewModel CreateSessionService()
    {
        var botService = new BotServiceViewModel(new StubClientStorage(), new StubClientLogger());
        return new SessionServiceViewModel(botService, new StubClientLogger());
    }

    private sealed class StubClientStorage : IClientStorage
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<ClientIdentity?> GetClientIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult((ClientIdentity?)null);
        public Task SaveClientIdentityAsync(ClientIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<BotProfile>> ListBotProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult((IReadOnlyList<BotProfile>)Array.Empty<BotProfile>());
        public Task UpsertBotProfileAsync(BotProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnownServer>> ListKnownServersAsync(CancellationToken cancellationToken = default) => Task.FromResult((IReadOnlyList<KnownServer>)Array.Empty<KnownServer>());
        public Task UpsertKnownServerAsync(KnownServer server, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ServerPluginCache?> GetServerPluginCacheAsync(string serverId, CancellationToken cancellationToken = default) => Task.FromResult((ServerPluginCache?)null);
        public Task UpsertServerPluginCacheAsync(ServerPluginCache cache, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentRuntimeState?> GetAgentRuntimeStateAsync(string botId, CancellationToken cancellationToken = default) => Task.FromResult((AgentRuntimeState?)null);
        public Task UpsertAgentRuntimeStateAsync(AgentRuntimeState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubClientLogger : IClientLogger
    {
        public void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? fields = null)
        {
        }
    }

    private static ServerSummaryItem CreateServerSummaryItem(string serverId, string host, int port, IReadOnlyDictionary<string, string> metadata)
    {
        return new ServerSummaryItem
        {
            ServerId = serverId,
            Name = serverId,
            Host = host,
            Port = port,
            UseTls = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = metadata,
            CachedPlugins = Array.Empty<PluginDescriptor>(),
            Endpoint = $"http://{host}:{port}",
            Status = "Status: reachable",
            AccentBrush = Brushes.Gray,
            BackgroundBrush = Brushes.White,
            VisualState = ServerCardVisualState.Live
        };
    }
}