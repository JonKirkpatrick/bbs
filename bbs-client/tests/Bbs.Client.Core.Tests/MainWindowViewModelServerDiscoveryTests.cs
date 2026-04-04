using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.App.ViewModels;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Tests;

public sealed class MainWindowViewModelServerDiscoveryTests
{
    [Fact]
    public async Task ProbeAndCatalogUseExplicitApiEndpoints()
    {
        const string catalogPayload = """
        [
          { "name": "counter", "display_name": "Counter" }
        ]
        """;

        await using var server = await TestApiServer.StartAsync(
            statusPayload: "{\"status\":\"ok\"}",
            gameCatalogPayload: catalogPayload);

        var vm = CreateViewModelWithHttpClient(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        });

        var knownServer = KnownServer.Create(
            serverId: "srv-1",
            name: "Test Server",
            host: "127.0.0.1",
            port: 6553,
            metadata: new Dictionary<string, string>
            {
                ["dashboard_endpoint"] = server.BaseUrl
            });

        var probeResult = await InvokeTupleTaskAsync(vm, "ProbeKnownServerOnceAsync", knownServer, CancellationToken.None);
        Assert.True((bool)probeResult[0]!);

        var fetchResult = await InvokeTupleTaskAsync(vm, "FetchServerPluginCatalogAsync", knownServer, CancellationToken.None);
        Assert.True((bool)fetchResult[0]!);
        var plugins = (IReadOnlyList<PluginDescriptor>)fetchResult[1]!;
        Assert.Single(plugins);
        Assert.Equal("counter", plugins[0].Name);
        Assert.Equal("api_game_catalog", (string)fetchResult[3]!);
    }

    [Fact]
    public async Task ProbeFailsWhenStatusAckIsNotOk()
    {
        await using var server = await TestApiServer.StartAsync(
            statusPayload: "{\"status\":\"err\"}",
            gameCatalogPayload: "[]");

        var vm = CreateViewModelWithHttpClient(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        });

        var knownServer = KnownServer.Create(
            serverId: "srv-2",
            name: "Bad Status Server",
            host: "203.0.113.1",
            port: 6554,
            metadata: new Dictionary<string, string>
            {
                ["dashboard_endpoint"] = server.BaseUrl
            });

        var probeResult = await InvokeTupleTaskAsync(vm, "ProbeKnownServerOnceAsync", knownServer, CancellationToken.None);
        Assert.False((bool)probeResult[0]!);
    }

    private static MainWindowViewModel CreateViewModelWithHttpClient(HttpClient client)
    {
#pragma warning disable SYSLIB0050
        var vm = (MainWindowViewModel)FormatterServices.GetUninitializedObject(typeof(MainWindowViewModel));
#pragma warning restore SYSLIB0050

        var field = typeof(MainWindowViewModel).GetField("_serverCatalogHttpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(vm, client);

        return vm;
    }

    private static async Task<ITuple> InvokeTupleTaskAsync(object instance, string methodName, params object[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var taskObj = method!.Invoke(instance, args);
        Assert.NotNull(taskObj);
        var task = (Task)taskObj!;
        await task.ConfigureAwait(false);

        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return (ITuple)result!;
    }

    private sealed class TestApiServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _worker;
        private readonly string _statusPayload;
        private readonly string _gameCatalogPayload;

        private TestApiServer(HttpListener listener, string baseUrl, string statusPayload, string gameCatalogPayload)
        {
            _listener = listener;
            BaseUrl = baseUrl.TrimEnd('/');
            _statusPayload = statusPayload;
            _gameCatalogPayload = gameCatalogPayload;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cts.Token));
        }

        public string BaseUrl { get; }

        public static async Task<TestApiServer> StartAsync(string statusPayload, string gameCatalogPayload)
        {
            var port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUrl + "/");
            listener.Start();

            var server = new TestApiServer(listener, baseUrl, statusPayload, gameCatalogPayload);
            await Task.Delay(50).ConfigureAwait(false);
            return server;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                var payload = path switch
                {
                    "/api/status" => _statusPayload,
                    "/api/game-catalog" => _gameCatalogPayload,
                    _ => "{\"status\":\"not_found\"}"
                };

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = path is "/api/status" or "/api/game-catalog"
                    ? (int)HttpStatusCode.OK
                    : (int)HttpStatusCode.NotFound;

                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                context.Response.OutputStream.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // no-op
            }
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
