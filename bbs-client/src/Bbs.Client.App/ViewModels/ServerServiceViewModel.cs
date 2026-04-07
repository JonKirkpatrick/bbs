using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

public sealed class ServerServiceViewModel : ViewModelBase
{
    // Constants for server probing and discovery
    private const int ServerProbeTimeoutMs = 1200;
    private const int ServerCatalogFetchTimeoutMs = 2000;
    private const int ServerCatalogSelectionRefreshCooldownMs = 5000;
    private const int DashboardPortFallback = 3000;
    private const int BotTcpDefaultPort = 8080;
    private const int ServerProbeMaxAttempts = 2;
    private const int ServerProbeRetryDelayMs = 200;

    // Metadata key constants
    private const string ProbeStatusMetadataKey = "probe_status";
    private const string ProbeLastCheckedMetadataKey = "probe_last_checked_utc";
    private const string ProbeLastErrorMetadataKey = "probe_last_error";
    private const string ClientOwnerTokenMetadataKey = "client.owner_token";

    private static readonly string[] DashboardEndpointMetadataKeys =
    {
        "dashboard_endpoint"
    };

    private static readonly string[] DashboardPortMetadataKeys =
    {
        "dashboard_port"
    };

    // Dependencies
    private IClientStorage _storage;
    private readonly IClientLogger _logger;
    private readonly HttpClient _httpClient;

    // Locking for probe cycles and catalog refresh
    private readonly object _serverProbeLock = new();
    private readonly object _serverCatalogRefreshLock = new();

    // Tracking collections
    private readonly Dictionary<string, DateTimeOffset> _serverCatalogLastRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _serverCatalogRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);

    // State fields
    private ServerSummaryItem? _selectedServer;
    private bool _isServerProbeInProgress;
    private bool _isServerDetailLoading;
    private string _serverCatalogStatus = "Select a server to view cached plugin catalog.";
    private string _serverEditorName = "new_server";
    private string _serverEditorHost = string.Empty;
    private string _serverEditorPort = BotTcpDefaultPort.ToString();
    private bool _serverEditorUseTls;
    private string _serverEditorMetadata = string.Empty;
    private string _serverEditorMessage = "Fill out the server form and save.";

    // Collections
    public ObservableCollection<ServerSummaryItem> Servers { get; }
    public ObservableCollection<ServerMetadataEntryItem> ServerMetadataEntries { get; }
    public ObservableCollection<ServerPluginCatalogItem> ServerPluginCatalogEntries { get; }

    public ServerServiceViewModel(IClientStorage storage, IClientLogger logger, HttpClient httpClient)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        Servers = new ObservableCollection<ServerSummaryItem>();
        ServerMetadataEntries = new ObservableCollection<ServerMetadataEntryItem>();
        ServerPluginCatalogEntries = new ObservableCollection<ServerPluginCatalogItem>();
    }

    public void UpdateStorage(IClientStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public ServerSummaryItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer == value)
            {
                return;
            }

            _selectedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
        }
    }

    public bool IsServerProbeInProgress
    {
        get => _isServerProbeInProgress;
        private set
        {
            if (_isServerProbeInProgress == value)
            {
                return;
            }

            _isServerProbeInProgress = value;
            OnPropertyChanged();
        }
    }

    public bool IsServerDetailLoading
    {
        get => _isServerDetailLoading;
        private set
        {
            if (_isServerDetailLoading == value)
            {
                return;
            }

            _isServerDetailLoading = value;
            OnPropertyChanged();
        }
    }

    public string ServerCatalogStatus
    {
        get => _serverCatalogStatus;
        private set
        {
            if (_serverCatalogStatus == value)
            {
                return;
            }

            _serverCatalogStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
        }
    }

    public string ServerEditorName
    {
        get => _serverEditorName;
        set
        {
            if (_serverEditorName == value)
            {
                return;
            }

            _serverEditorName = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorHost
    {
        get => _serverEditorHost;
        set
        {
            if (_serverEditorHost == value)
            {
                return;
            }

            _serverEditorHost = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorPort
    {
        get => _serverEditorPort;
        set
        {
            if (_serverEditorPort == value)
            {
                return;
            }

            _serverEditorPort = value;
            OnPropertyChanged();
        }
    }

    public bool ServerEditorUseTls
    {
        get => _serverEditorUseTls;
        set
        {
            if (_serverEditorUseTls == value)
            {
                return;
            }

            _serverEditorUseTls = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMetadata
    {
        get => _serverEditorMetadata;
        set
        {
            if (_serverEditorMetadata == value)
            {
                return;
            }

            _serverEditorMetadata = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMessage
    {
        get => _serverEditorMessage;
        set
        {
            if (_serverEditorMessage == value)
            {
                return;
            }

            _serverEditorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool HasSelectedServer => SelectedServer is not null;
    public bool HasServerMetadata => ServerMetadataEntries.Count > 0;
    public bool HasServerPluginCatalog => ServerPluginCatalogEntries.Count > 0;
    public bool ShowServerMetadataEmpty => !IsServerDetailLoading && !HasServerMetadata;
    public bool ShowServerPluginCatalogEmpty => !IsServerDetailLoading && !HasServerPluginCatalog;

    public void PrepareForNewServer()
    {
        ServerEditorName = "new_server";
        ServerEditorHost = string.Empty;
        ServerEditorPort = BotTcpDefaultPort.ToString();
        ServerEditorUseTls = false;
        ServerEditorMetadata = string.Empty;
        ServerEditorMessage = "Creating a new server profile.";
    }

    public void PopulateEditor(ServerSummaryItem server)
    {
        ServerEditorName = server.Name;
        ServerEditorHost = server.Host;
        ServerEditorPort = server.Port.ToString();
        ServerEditorUseTls = server.UseTls;
        ServerEditorMetadata = MainWindowViewModelHelpers.FormatMetadata(server.Metadata);
        ServerEditorMessage = "Edit the server form and save.";
    }

    public async void TriggerStartupProbe()
    {
        _ = await RunServerProbeCycleAsync(trigger: "startup", updateEditorStatus: false);
    }

    public async void TriggerManualProbe()
    {
        var result = await RunServerProbeCycleAsync(trigger: "manual", updateEditorStatus: true);
        if (!result.Succeeded)
        {
            ServerEditorMessage = "Probe failed. See logs for details.";
        }
    }

    public void RefreshSelectedServerDetail(ServerSummaryItem? selectedServer)
    {
        IsServerDetailLoading = true;
        ServerMetadataEntries.Clear();
        ServerPluginCatalogEntries.Clear();

        if (selectedServer is null)
        {
            ServerCatalogStatus = "Select a server to view cached plugin catalog.";
            IsServerDetailLoading = false;
            OnPropertyChanged(nameof(HasServerMetadata));
            OnPropertyChanged(nameof(HasServerPluginCatalog));
            OnPropertyChanged(nameof(ShowServerMetadataEmpty));
            OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));
            return;
        }

        foreach (var metadata in selectedServer.Metadata)
        {
            ServerMetadataEntries.Add(new ServerMetadataEntryItem(metadata.Key, metadata.Value));
        }

        foreach (var plugin in selectedServer.CachedPlugins)
        {
            ServerPluginCatalogEntries.Add(new ServerPluginCatalogItem(plugin.Name, plugin.DisplayName, plugin.Version, plugin.Metadata));
        }

        ServerCatalogStatus = selectedServer.CachedPlugins.Count == 0
            ? "No cached plugins available."
            : $"Cached plugins: {selectedServer.CachedPlugins.Count}";

        IsServerDetailLoading = false;
        OnPropertyChanged(nameof(HasServerMetadata));
        OnPropertyChanged(nameof(HasServerPluginCatalog));
        OnPropertyChanged(nameof(ShowServerMetadataEmpty));
        OnPropertyChanged(nameof(ShowServerPluginCatalogEmpty));

        TriggerSelectedServerCatalogRefresh(selectedServer.ServerId);
    }

    public async void LoadServersFromStorage()
    {
        var servers = await _storage.ListKnownServersAsync();

        Servers.Clear();
        foreach (var server in servers)
        {
            var cache = await _storage.GetServerPluginCacheAsync(server.ServerId);
            Servers.Add(ServerSummaryItem.FromKnownServer(server, cache));
        }

        OnPropertyChanged(nameof(Servers));
    }

    public ServerSummaryItem? FindServerById(string serverId)
    {
        foreach (var server in Servers)
        {
            if (server.ServerId == serverId)
            {
                return server;
            }
        }

        return null;
    }

    // Probing orchestration
    private async Task<(bool Succeeded, int ReachableCount, int UnreachableCount)> RunServerProbeCycleAsync(string trigger, bool updateEditorStatus)
    {
        if (!TryBeginProbeCycle())
        {
            if (updateEditorStatus)
            {
                ServerEditorMessage = "Probe already in progress.";
            }

            return (false, 0, 0);
        }

        if (updateEditorStatus)
        {
            ServerEditorMessage = "Probing known servers...";
        }

        try
        {
            var result = await ProbeKnownServersAsync(CancellationToken.None);

            if (updateEditorStatus)
            {
                ServerEditorMessage = $"Probe complete: {result.ReachableCount} reachable, {result.UnreachableCount} unreachable.";
            }

            _logger.Log(LogLevel.Information, "server_probe_cycle_completed", "Known server probe cycle completed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["reachable"] = result.ReachableCount.ToString(),
                    ["unreachable"] = result.UnreachableCount.ToString()
                });

            return (true, result.ReachableCount, result.UnreachableCount);
        }
        catch (Exception ex)
        {
            if (updateEditorStatus)
            {
                ServerEditorMessage = "Probe failed. See logs for details.";
            }

            _logger.Log(LogLevel.Warning, "server_probe_cycle_failed", "Known server probe cycle failed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["error"] = ex.GetType().Name
                });

            return (false, 0, 0);
        }
        finally
        {
            EndProbeCycle();
        }
    }

    private async Task<(int ReachableCount, int UnreachableCount)> ProbeKnownServersAsync(CancellationToken cancellationToken)
    {
        var knownServers = await _storage.ListKnownServersAsync(cancellationToken);
        if (knownServers.Count == 0)
        {
            return (0, 0);
        }

        var reachableCount = 0;
        var unreachableCount = 0;

        foreach (var knownServer in knownServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probeResult = await ProbeKnownServerWithRetryAsync(knownServer, cancellationToken);
            var metadata = new Dictionary<string, string>(knownServer.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [ProbeStatusMetadataKey] = probeResult.IsReachable ? "reachable" : "unreachable",
                [ProbeLastCheckedMetadataKey] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (probeResult.IsReachable)
            {
                metadata.Remove(ProbeLastErrorMetadataKey);
                reachableCount++;

                var pluginCatalogResult = await RefreshServerPluginCatalogCacheAsync(knownServer, cancellationToken);
                if (pluginCatalogResult.Updated)
                {
                    metadata["probe_plugin_count"] = pluginCatalogResult.PluginCount.ToString();
                    metadata["probe_plugin_sync_utc"] = DateTimeOffset.UtcNow.ToString("O");
                }
                else if (!string.IsNullOrWhiteSpace(pluginCatalogResult.ErrorCode))
                {
                    metadata["probe_plugin_error"] = pluginCatalogResult.ErrorCode;
                }

                var ownerTokenResult = await EnsureServerOwnerTokenAsync(knownServer, metadata, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ownerTokenResult.OwnerToken))
                {
                    metadata[ClientOwnerTokenMetadataKey] = ownerTokenResult.OwnerToken;
                }
            }
            else
            {
                metadata[ProbeLastErrorMetadataKey] = probeResult.ErrorCode;
                unreachableCount++;
            }

            var updatedServer = KnownServer.Create(
                serverId: knownServer.ServerId,
                name: knownServer.Name,
                host: knownServer.Host,
                port: knownServer.Port,
                useTls: knownServer.UseTls,
                metadata: metadata,
                createdAtUtc: knownServer.CreatedAtUtc,
                updatedAtUtc: DateTimeOffset.UtcNow);

            await _storage.UpsertKnownServerAsync(updatedServer, cancellationToken);
            _logger.Log(LogLevel.Information, "server_probe_result", "Probe result for known server.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["host"] = knownServer.Host,
                    ["port"] = knownServer.Port.ToString(),
                    ["reachable"] = probeResult.IsReachable.ToString(),
                    ["attempts"] = probeResult.Attempts.ToString(),
                    ["error"] = probeResult.ErrorCode
                });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedServerId = SelectedServer?.ServerId;
            LoadServersFromStorage();
            if (!string.IsNullOrWhiteSpace(selectedServerId))
            {
                SelectedServer = FindServerById(selectedServerId);
            }
        });

        return (reachableCount, unreachableCount);
    }

    private bool TryBeginProbeCycle()
    {
        lock (_serverProbeLock)
        {
            if (IsServerProbeInProgress)
            {
                return false;
            }

            IsServerProbeInProgress = true;
            return true;
        }
    }

    private void EndProbeCycle()
    {
        lock (_serverProbeLock)
        {
            IsServerProbeInProgress = false;
        }
    }

    private async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerWithRetryAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= ServerProbeMaxAttempts; attempt++)
        {
            var singleResult = await ProbeKnownServerOnceAsync(knownServer, cancellationToken);
            if (singleResult.IsReachable)
            {
                return (true, attempt, string.Empty);
            }

            lastError = singleResult.ErrorCode;
            if (attempt < ServerProbeMaxAttempts)
            {
                await Task.Delay(ServerProbeRetryDelayMs, cancellationToken);
            }
        }

        return (false, ServerProbeMaxAttempts, lastError ?? "probe_failed");
    }

    private async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerOnceAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ServerProbeTimeoutMs);

        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        var observedErrors = new List<string>();

        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var statusUri = new Uri(endpoint + "/api/status");
                using var response = await _httpClient.GetAsync(statusUri, probeCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    observedErrors.Add($"status_http_{(int)response.StatusCode}_{endpoint}");
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(probeCts.Token);
                if (TryIsStatusAck(payload))
                {
                    return (true, 1, string.Empty);
                }

                observedErrors.Add($"status_payload_invalid_{endpoint}");
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"status_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"status_http_error_{endpoint}");
            }
            catch (JsonException)
            {
                observedErrors.Add($"status_payload_json_error_{endpoint}");
            }
        }

        var errorCode = observedErrors.Count == 0
            ? "status_probe_failed"
            : $"status_probe_failed:{string.Join(',', observedErrors)}";

        return (false, 1, errorCode);
    }

    private static bool TryIsStatusAck(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        return MainWindowViewModelHelpers.IsStatusOkObject(doc.RootElement);
    }

    private void TriggerSelectedServerCatalogRefresh(string serverId)
    {
        if (!ShouldRefreshServerCatalog(serverId))
        {
            return;
        }

        _ = RefreshSelectedServerCatalogAsync(serverId);
    }

    private bool ShouldRefreshServerCatalog(string serverId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_serverCatalogRefreshLock)
        {
            if (_serverCatalogRefreshInFlight.Contains(serverId))
            {
                return false;
            }

            if (_serverCatalogLastRefreshUtc.TryGetValue(serverId, out var lastRefresh) &&
                now - lastRefresh < TimeSpan.FromMilliseconds(ServerCatalogSelectionRefreshCooldownMs))
            {
                return false;
            }

            _serverCatalogRefreshInFlight.Add(serverId);
            return true;
        }
    }

    private void MarkServerCatalogRefreshComplete(string serverId)
    {
        lock (_serverCatalogRefreshLock)
        {
            _serverCatalogRefreshInFlight.Remove(serverId);
            _serverCatalogLastRefreshUtc[serverId] = DateTimeOffset.UtcNow;
        }
    }

    private async Task RefreshSelectedServerCatalogAsync(string serverId)
    {
        try
        {
            var knownServer = (await _storage.ListKnownServersAsync()).FirstOrDefault(s => s.ServerId == serverId);
            if (knownServer is null)
            {
                return;
            }

            var result = await RefreshServerPluginCatalogCacheAsync(knownServer, CancellationToken.None);
            if (!result.Updated)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedServerId = SelectedServer?.ServerId;
                LoadServersFromStorage();
                if (!string.IsNullOrWhiteSpace(selectedServerId))
                {
                    SelectedServer = FindServerById(selectedServerId);
                }
            });
        }
        finally
        {
            MarkServerCatalogRefreshComplete(serverId);
        }
    }

    private async Task<(bool Updated, int PluginCount, string ErrorCode)> RefreshServerPluginCatalogCacheAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var fetchResult = await FetchServerPluginCatalogAsync(knownServer, cancellationToken);
        if (!fetchResult.Succeeded)
        {
            _logger.Log(LogLevel.Warning, "server_plugin_catalog_fetch_failed", "Failed to fetch server plugin catalog.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["reason"] = fetchResult.ErrorCode
                });

            return (false, 0, fetchResult.ErrorCode);
        }

        var cache = ServerPluginCache.Create(knownServer.ServerId, fetchResult.Plugins, DateTimeOffset.UtcNow);
        await _storage.UpsertServerPluginCacheAsync(cache, cancellationToken);

        _logger.Log(LogLevel.Information, "server_plugin_catalog_cached", "Server plugin catalog refreshed.",
            new Dictionary<string, string>
            {
                ["server_id"] = knownServer.ServerId,
                ["plugin_count"] = cache.Plugins.Count.ToString(),
                ["source"] = fetchResult.Source
            });

        return (true, cache.Plugins.Count, string.Empty);
    }

    private async Task<(bool Succeeded, IReadOnlyList<PluginDescriptor> Plugins, string ErrorCode, string Source)> FetchServerPluginCatalogAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        var observedErrors = new List<string>();

        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var apiCatalogUri = new Uri(endpoint + "/api/game-catalog");
                using var apiResponse = await _httpClient.GetAsync(apiCatalogUri, cancellationToken);
                if (apiResponse.IsSuccessStatusCode)
                {
                    var jsonPayload = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (ServerPluginCatalogParser.TryParseFromJsonCatalog(jsonPayload, out var plugins, out _))
                    {
                        return (true, plugins, string.Empty, "api_game_catalog");
                    }

                    observedErrors.Add($"api_parse_failed_{endpoint}");
                }
                else
                {
                    observedErrors.Add($"api_http_{(int)apiResponse.StatusCode}_{endpoint}");
                }
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"api_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"api_http_error_{endpoint}");
            }
        }

        var errorCode = observedErrors.Count == 0
            ? "plugin_catalog_fetch_failed"
            : $"plugin_catalog_fetch_failed:{string.Join(',', observedErrors)}";

        return (false, Array.Empty<PluginDescriptor>(), errorCode, "api_game_catalog");
    }

    private async Task<(bool Succeeded, string OwnerToken)> EnsureServerOwnerTokenAsync(KnownServer knownServer, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var existingToken = metadata.TryGetValue(ClientOwnerTokenMetadataKey, out var rawExisting) && !string.IsNullOrWhiteSpace(rawExisting)
            ? rawExisting.Trim()
            : string.Empty;

        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var uri = string.IsNullOrWhiteSpace(existingToken)
                    ? new Uri(endpoint + "/api/owner-token")
                    : new Uri(endpoint + "/api/owner-token?owner_token=" + Uri.EscapeDataString(existingToken));

                using var response = await _httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!TryParseOwnerTokenResponse(payload, out var token) || string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                return (true, token);
            }
            catch (TaskCanceledException)
            {
                continue;
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return (false, existingToken);
    }

    private static bool TryParseOwnerTokenResponse(string payload, out string ownerToken)
    {
        ownerToken = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!MainWindowViewModelHelpers.IsStatusOkObject(root))
        {
            return false;
        }

        if (!root.TryGetProperty("owner_token", out var ownerTokenNode))
        {
            return false;
        }

        ownerToken = ownerTokenNode.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(ownerToken);
    }

    internal static IReadOnlyList<string> BuildServerBaseEndpointCandidates(KnownServer knownServer)
    {
        var preferredScheme = knownServer.UseTls ? "https" : "http";
        var alternateScheme = knownServer.UseTls ? "http" : "https";
        var endpoints = new List<string>();

        var metadataDashboardEndpoint = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(knownServer.Metadata, DashboardEndpointMetadataKeys);
        if (Uri.TryCreate(metadataDashboardEndpoint, UriKind.Absolute, out var dashboardUri))
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(dashboardUri.Scheme, dashboardUri.Host, dashboardUri.Port));
        }

        var metadataDashboardPort = MainWindowViewModelHelpers.ParsePositivePort(knownServer.Metadata, DashboardPortMetadataKeys);
        if (metadataDashboardPort is not null)
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, metadataDashboardPort.Value));
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, metadataDashboardPort.Value));
        }

        endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, knownServer.Port));
        endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, knownServer.Port));

        if (knownServer.Port != DashboardPortFallback)
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, DashboardPortFallback));
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, DashboardPortFallback));
        }

        return endpoints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Helper: Normalize dashboard endpoint with fallback logic
    public static string NormalizeDashboardEndpoint(string rawEndpoint, string rawHost, string rawPort, bool useTls)
    {
        var scheme = useTls ? "https" : "http";
        if (Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (!string.IsNullOrWhiteSpace(rawEndpoint))
        {
            var withScheme = $"{scheme}://{rawEndpoint}";
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out absolute))
            {
                return absolute.ToString();
            }
        }

        if (!string.IsNullOrWhiteSpace(rawHost) && !string.IsNullOrWhiteSpace(rawPort))
        {
            var withScheme = $"{scheme}://{rawHost}:{rawPort}";
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out absolute))
            {
                return absolute.ToString();
            }
        }

        return string.Empty;
    }
}
