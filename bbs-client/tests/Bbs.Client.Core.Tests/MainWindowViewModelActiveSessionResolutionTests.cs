using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using Avalonia.Media;
using Bbs.Client.App.ViewModels;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class MainWindowViewModelActiveSessionResolutionTests
{
    [Fact]
    public void ResolveServerIdFromAgentTarget_MatchesUsingBotPortMetadata()
    {
        var vm = CreateViewModel(
            servers: new[]
            {
                CreateServerSummaryItem("srv-default", "127.0.0.1", 3000, new Dictionary<string, string>()),
                CreateServerSummaryItem("srv-bot-port", "127.0.0.1", 3001, new Dictionary<string, string>
                {
                    ["bot_port"] = "9090"
                })
            });

        var resolved = InvokePrivate<string>(vm, "ResolveServerIdFromAgentTarget", "127.0.0.1:9090");

        Assert.Equal("srv-bot-port", resolved);
    }

    [Fact]
    public void ResolveServerIdFromAgentTarget_ReturnsEmptyWhenHostOnlyIsAmbiguous()
    {
        var vm = CreateViewModel(
            servers: new[]
            {
                CreateServerSummaryItem("srv-a", "localhost", 3000, new Dictionary<string, string>()),
                CreateServerSummaryItem("srv-b", "127.0.0.1", 3001, new Dictionary<string, string>())
            });

        var resolved = InvokePrivate<string>(vm, "ResolveServerIdFromAgentTarget", "localhost");

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void ResolveServerIdFromAgentTarget_ReturnsEmptyWhenNoServerMatches()
    {
        var vm = CreateViewModel(
            servers: new[]
            {
                CreateServerSummaryItem("srv-a", "192.0.2.10", 3000, new Dictionary<string, string>())
            });

        var resolved = InvokePrivate<string>(vm, "ResolveServerIdFromAgentTarget", "203.0.113.50:8080");

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void BuildArenaOptionsForServer_PopulatesForResolvedServerId()
    {
        var selectedServer = CreateServerSummaryItem(
            "srv-join",
            "localhost",
            3000,
            new Dictionary<string, string>
            {
                ["bot_port"] = "8080"
            });

        var vm = CreateViewModel(
            servers: new[] { selectedServer },
            selectedServer: selectedServer,
            arenas: new[]
            {
                new ServerArenaItem(12, "Counter", "running", "1/2", 7, "http://example/12", "http://example/p12", 800, 600, new RelayCommand(() => { })),
                new ServerArenaItem(18, "Fhourstones", "waiting", "0/2", 0, "http://example/18", "http://example/p18", 800, 600, new RelayCommand(() => { }))
            });

        var resolvedServerId = InvokePrivate<string>(vm, "ResolveServerIdFromAgentTarget", "127.0.0.1:8080");
        var options = InvokePrivate<ObservableCollection<ServerArenaOptionItem>>(vm, "BuildArenaOptionsForServer", resolvedServerId);

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

        var envelope = InvokePrivateStatic<AgentControlResponse>("ParseControlEnvelope", line);

        Assert.Equal("server_access", envelope.Type);
        Assert.Equal("req-1", envelope.Id);
        Assert.Equal("127.0.0.1:8080", envelope.Server);
        Assert.Equal("42", envelope.SessionId);
        Assert.Equal("bot-123", envelope.BotId);
        Assert.Equal("3000", envelope.DashboardPort);
    }

    private static MainWindowViewModel CreateViewModel(
        IReadOnlyList<ServerSummaryItem>? servers = null,
        ServerSummaryItem? selectedServer = null,
        IReadOnlyList<ServerArenaItem>? arenas = null)
    {
#pragma warning disable SYSLIB0050
        var vm = (MainWindowViewModel)FormatterServices.GetUninitializedObject(typeof(MainWindowViewModel));
#pragma warning restore SYSLIB0050

        SetField(vm, "<Servers>k__BackingField", new ObservableCollection<ServerSummaryItem>(servers ?? Array.Empty<ServerSummaryItem>()));
        SetField(vm, "<ServerArenaEntries>k__BackingField", new ObservableCollection<ServerArenaItem>(arenas ?? Array.Empty<ServerArenaItem>()));
        SetField(vm, "_selectedServer", selectedServer);

        return vm;
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

    private static T InvokePrivate<T>(object instance, string methodName, params object[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var value = method!.Invoke(instance, args);
        Assert.NotNull(value);

        return (T)value!;
    }

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var value = method!.Invoke(null, args);
        Assert.NotNull(value);

        return (T)value!;
    }
}
