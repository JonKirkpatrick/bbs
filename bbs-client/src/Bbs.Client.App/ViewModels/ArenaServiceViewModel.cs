using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel managing arena editor form state and initialization workflows.
/// Extracted from MainWindowViewModel to reduce complexity and improve testability.
/// </summary>
public sealed class ArenaServiceViewModel : ViewModelBase
{
    private const int ArenaWatcherPollIntervalMs = 900;

    private readonly HttpClient _serverCatalogHttpClient;
    private ObservableCollection<ServerPluginCatalogItem>? _pluginCatalog;
    private CancellationTokenSource? _arenaViewerWatchCts;
    private int _watchedArenaId;
    private ServerSummaryItem? _watchedServer;
    private int _serverArenasRefreshVersion;
    private string _ownerArenaSelectedPlugin = string.Empty;
    private string _ownerArenaArgs = string.Empty;
    private string _ownerArenaTimeMs = string.Empty;
    private bool _ownerArenaAllowHandicap = true;
    private string _ownerJoinArenaId = string.Empty;
    private string _ownerJoinHandicapPercent = "0";

    private bool _isServerArenasLoading;
    private string _serverArenasStatus = "Select a server to load active arenas.";
    private string _arenaViewerLabel = "Arena Viewer";
    private string _arenaViewerStatus = "Select an arena to watch.";
    private string _arenaViewerRawState = string.Empty;
    private string _arenaViewerUrl = string.Empty;
    private string _arenaViewerPluginEntryUrl = string.Empty;
    private double _arenaViewerHostWidth = 760;
    private double _arenaViewerHostHeight = 340;
    private bool _isEmbeddedViewerSupported = true;
    private string _embeddedViewerSupportMessage = "Embedded JS viewer is available.";
    private string _arenaViewerLastUpdatedUtc = "-";
    private string _arenaViewerLastError = string.Empty;

    public ObservableCollection<ServerArenaItem> ServerArenaEntries { get; } = new();

    public ArenaServiceViewModel(HttpClient serverCatalogHttpClient)
    {
        _serverCatalogHttpClient = serverCatalogHttpClient;
    }

    public bool IsServerArenasLoading
    {
        get => _isServerArenasLoading;
        set
        {
            if (_isServerArenasLoading == value)
            {
                return;
            }

            _isServerArenasLoading = value;
            OnPropertyChanged();
        }
    }

    public string ServerArenasStatus
    {
        get => _serverArenasStatus;
        set
        {
            if (_serverArenasStatus == value)
            {
                return;
            }

            _serverArenasStatus = value;
            OnPropertyChanged();
        }
    }

    public string ArenaViewerLabel
    {
        get => _arenaViewerLabel;
        set
        {
            if (_arenaViewerLabel == value)
            {
                return;
            }

            _arenaViewerLabel = value;
            OnPropertyChanged();
        }
    }

    public string ArenaViewerStatus
    {
        get => _arenaViewerStatus;
        set
        {
            if (_arenaViewerStatus == value)
            {
                return;
            }

            _arenaViewerStatus = value;
            OnPropertyChanged();
        }
    }

    public string ArenaViewerRawState
    {
        get => _arenaViewerRawState;
        set
        {
            if (_arenaViewerRawState == value)
            {
                return;
            }

            _arenaViewerRawState = value;
            OnPropertyChanged();
        }
    }

    public string ArenaViewerUrl
    {
        get => _arenaViewerUrl;
        set
        {
            if (_arenaViewerUrl == value)
            {
                return;
            }

            _arenaViewerUrl = value;
            OnPropertyChanged();
        }
    }

    public string ArenaViewerPluginEntryUrl
    {
        get => _arenaViewerPluginEntryUrl;
        set
        {
            if (_arenaViewerPluginEntryUrl == value)
            {
                return;
            }

            _arenaViewerPluginEntryUrl = value;
            OnPropertyChanged();
        }
    }

    public double ArenaViewerHostWidth
    {
        get => _arenaViewerHostWidth;
        set
        {
            if (Math.Abs(_arenaViewerHostWidth - value) < 0.5)
            {
                return;
            }

            _arenaViewerHostWidth = value;
            OnPropertyChanged();
        }
    }

    public double ArenaViewerHostHeight
    {
        get => _arenaViewerHostHeight;
        set
        {
            if (Math.Abs(_arenaViewerHostHeight - value) < 0.5)
            {
                return;
            }

            _arenaViewerHostHeight = value;
            OnPropertyChanged();
        }
    }

    public bool IsEmbeddedViewerSupported
    {
        get => _isEmbeddedViewerSupported;
        set
        {
            if (_isEmbeddedViewerSupported == value)
            {
                return;
            }

            _isEmbeddedViewerSupported = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowEmbeddedViewerFallback));
            OnPropertyChanged(nameof(ShowOpenArenaViewerButton));
            OnPropertyChanged(nameof(ArenaViewerDiagnostics));
        }
    }

    public bool ShowEmbeddedViewerFallback => !IsEmbeddedViewerSupported;

    public bool ShowOpenArenaViewerButton => !IsEmbeddedViewerSupported;

    public string EmbeddedViewerSupportMessage
    {
        get => _embeddedViewerSupportMessage;
        set
        {
            if (_embeddedViewerSupportMessage == value)
            {
                return;
            }

            _embeddedViewerSupportMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArenaViewerDiagnostics));
        }
    }

    public string ArenaViewerLastUpdatedUtc
    {
        get => _arenaViewerLastUpdatedUtc;
        set
        {
            if (_arenaViewerLastUpdatedUtc == value)
            {
                return;
            }

            _arenaViewerLastUpdatedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArenaViewerDiagnostics));
        }
    }

    public string ArenaViewerLastError
    {
        get => _arenaViewerLastError;
        set
        {
            if (_arenaViewerLastError == value)
            {
                return;
            }

            _arenaViewerLastError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArenaViewerDiagnostics));
        }
    }

    public string ArenaViewerDiagnostics =>
        $"Embedded viewer: {(IsEmbeddedViewerSupported ? "ready" : "fallback")}\n" +
        $"Capability: {EmbeddedViewerSupportMessage}\n" +
        $"Last arena update: {ArenaViewerLastUpdatedUtc}\n" +
        $"Last error: {(string.IsNullOrWhiteSpace(ArenaViewerLastError) ? "none" : ArenaViewerLastError)}";

    public void RefreshServerArenas(ServerSummaryItem? selectedServer, Action refreshActiveSessionArenaOptions, Action enterArenaViewerContext)
    {
        _watchedServer = selectedServer;
        _ = RefreshSelectedServerArenasAsync(selectedServer, refreshActiveSessionArenaOptions, enterArenaViewerContext);
    }

    public void StartWatchingArena(ServerSummaryItem? selectedServer, int arenaId, string game, string viewerUrl, string pluginEntryUrl, int viewerWidth, int viewerHeight, Action enterArenaViewerContext)
    {
        _watchedServer = selectedServer;
        _watchedArenaId = arenaId;
        ArenaViewerLabel = $"Arena #{arenaId} ({(string.IsNullOrWhiteSpace(game) ? "unknown" : game)})";
        ApplyArenaViewerProjection(viewerUrl, pluginEntryUrl, viewerWidth, viewerHeight, overwriteUrlsWhenEmpty: true);
        ArenaViewerStatus = "Starting live arena watch...";
        ArenaViewerRawState = string.Empty;
        ArenaViewerLastError = string.Empty;

        enterArenaViewerContext();

        if (!IsEmbeddedViewerSupported)
        {
            ArenaViewerStatus = "Embedded viewer unavailable; using fallback mode with raw state and external viewer link.";
        }

        StartArenaViewerWatchLoop(enterArenaViewerContext);
    }

    public void StopArenaViewerWatch()
    {
        var cts = _arenaViewerWatchCts;
        _arenaViewerWatchCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void OpenArenaViewerInBrowser()
    {
        if (string.IsNullOrWhiteSpace(ArenaViewerUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ArenaViewerUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            ArenaViewerStatus = "Unable to launch system browser for this arena viewer URL.";
        }
    }

    public void UpdateArenaViewerFromArena(ServerArenaApiDto arena)
    {
        ArenaViewerStatus = $"Arena {arena.ArenaId} | {arena.Status} | Moves: {arena.MoveCount}";
        ArenaViewerRawState = string.IsNullOrWhiteSpace(arena.GameState)
            ? "(no game state reported)"
            : arena.GameState;
        ArenaViewerLastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
        ArenaViewerLastError = string.Empty;
        ApplyArenaViewerProjection(arena.ViewerUrl, arena.PluginEntryUrl, arena.ViewerWidth, arena.ViewerHeight, overwriteUrlsWhenEmpty: false);
    }

    public void ApplyArenaViewerProjection(string viewerUrl, string pluginEntryUrl, int viewerWidth, int viewerHeight, bool overwriteUrlsWhenEmpty)
    {
        if (overwriteUrlsWhenEmpty || !string.IsNullOrWhiteSpace(viewerUrl))
        {
            ArenaViewerUrl = viewerUrl;
        }

        if (overwriteUrlsWhenEmpty || !string.IsNullOrWhiteSpace(pluginEntryUrl))
        {
            ArenaViewerPluginEntryUrl = pluginEntryUrl;
        }

        ArenaViewerHostWidth = Math.Max(300, viewerWidth > 0 ? viewerWidth : 760);
        ArenaViewerHostHeight = Math.Max(200, viewerHeight > 0 ? viewerHeight : 340);
    }

    public string OwnerArenaSelectedPlugin
    {
        get => _ownerArenaSelectedPlugin;
        set
        {
            if (string.Equals(_ownerArenaSelectedPlugin, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaSelectedPlugin = value;
            OnPropertyChanged();
            SyncArgsFromSelectedPlugin(_pluginCatalog);
        }
    }

    public string OwnerArenaArgs
    {
        get => _ownerArenaArgs;
        set
        {
            if (string.Equals(_ownerArenaArgs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaArgs = value;
            OnPropertyChanged();
        }
    }

    public string OwnerArenaTimeMs
    {
        get => _ownerArenaTimeMs;
        set
        {
            if (string.Equals(_ownerArenaTimeMs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaTimeMs = value;
            OnPropertyChanged();
        }
    }

    public bool OwnerArenaAllowHandicap
    {
        get => _ownerArenaAllowHandicap;
        set
        {
            if (_ownerArenaAllowHandicap == value)
            {
                return;
            }

            _ownerArenaAllowHandicap = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinArenaId
    {
        get => _ownerJoinArenaId;
        set
        {
            if (string.Equals(_ownerJoinArenaId, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinArenaId = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinHandicapPercent
    {
        get => _ownerJoinHandicapPercent;
        set
        {
            if (string.Equals(_ownerJoinHandicapPercent, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinHandicapPercent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Resets all arena form fields to default values.
    /// Called when preparing to create or modify an arena context.
    /// </summary>
    public void PrepareForArenaForm()
    {
        OwnerArenaSelectedPlugin = string.Empty;
        OwnerArenaArgs = string.Empty;
        OwnerArenaTimeMs = string.Empty;
        OwnerArenaAllowHandicap = true;
        OwnerJoinArenaId = string.Empty;
        OwnerJoinHandicapPercent = "0";
    }

    /// <summary>
    /// Ensures a valid plugin is selected from the available plugin catalog.
    /// If the current selection is invalid or no plugins are available, selects the first plugin or clears.
    /// </summary>
    /// <param name="pluginCatalog">Collection of available server plugins.</param>
    public void EnsureValidPluginSelection(ObservableCollection<ServerPluginCatalogItem> pluginCatalog)
    {
        _pluginCatalog = pluginCatalog;

        if (pluginCatalog.Count == 0)
        {
            OwnerArenaSelectedPlugin = string.Empty;
            OwnerArenaArgs = string.Empty;
            return;
        }

        var exists = pluginCatalog.Any(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            OwnerArenaSelectedPlugin = pluginCatalog[0].Name;
            return;
        }

        SyncArgsFromSelectedPlugin(pluginCatalog);
    }

    public void SetPluginCatalog(ObservableCollection<ServerPluginCatalogItem> pluginCatalog)
    {
        _pluginCatalog = pluginCatalog;
    }

    /// <summary>
    /// Synchronizes arena args from the selected plugin's metadata.
    /// </summary>
    /// <param name="pluginCatalog">Optional collection of available plugins. If null, uses only current SelectedPlugin.</param>
    private void SyncArgsFromSelectedPlugin(ObservableCollection<ServerPluginCatalogItem>? pluginCatalog = null)
    {
        if (string.IsNullOrWhiteSpace(OwnerArenaSelectedPlugin))
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        ServerPluginCatalogItem? selectedPlugin;
        if (pluginCatalog is not null)
        {
            selectedPlugin = pluginCatalog
                .FirstOrDefault(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // If no catalog provided, we can't sync - this is a limitation when called from property setter
            return;
        }

        if (selectedPlugin is null)
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        if (!selectedPlugin.Metadata.TryGetValue("args_json", out var argsJson) || string.IsNullOrWhiteSpace(argsJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var parts = new List<string>();
            foreach (var arg in doc.RootElement.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = arg.TryGetProperty("key", out var keyElement)
                    ? keyElement.GetString()
                    : null;
                var defaultValue = arg.TryGetProperty("default_value", out var defaultElement)
                    ? defaultElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(defaultValue))
                {
                    continue;
                }

                parts.Add($"{key.Trim()}={defaultValue.Trim()}");
            }

            if (parts.Count > 0)
            {
                OwnerArenaArgs = string.Join(' ', parts);
            }
        }
        catch (JsonException)
        {
        }
    }

    internal static string BuildArenaPlayersText(string player1Name, string player2Name)
    {
        var p1 = string.IsNullOrWhiteSpace(player1Name) ? "-" : player1Name;
        var p2 = string.IsNullOrWhiteSpace(player2Name) ? "-" : player2Name;
        return $"{p1} vs {p2}";
    }

    internal static KnownServer ToKnownServer(ServerSummaryItem server)
    {
        return KnownServer.Create(
            serverId: server.ServerId,
            name: server.Name,
            host: server.Host,
            port: server.Port,
            useTls: server.UseTls,
            metadata: new Dictionary<string, string>(server.Metadata),
            createdAtUtc: server.CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);
    }

    internal static bool TryParseArenaList(string payload, out IReadOnlyList<ServerArenaApiDto> arenas)
    {
        arenas = Array.Empty<ServerArenaApiDto>();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!MainWindowViewModelHelpers.IsStatusOkObject(doc.RootElement))
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("arenas", out var arenasNode) || arenasNode.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<ServerArenaApiDto>();
            foreach (var arena in arenasNode.EnumerateArray())
            {
                if (arena.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var arenaId = arena.TryGetProperty("arena_id", out var idNode) ? idNode.GetInt32() : 0;
                if (arenaId <= 0)
                {
                    continue;
                }

                list.Add(new ServerArenaApiDto
                {
                    ArenaId = arenaId,
                    Game = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "game"),
                    Status = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "status"),
                    Player1Name = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "player1_name"),
                    Player2Name = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "player2_name"),
                    MoveCount = MainWindowViewModelHelpers.GetIntPropertyOrDefault(arena, "move_count"),
                    GameState = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "game_state"),
                    ViewerUrl = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "viewer_url"),
                    PluginEntryUrl = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(arena, "plugin_entry_url"),
                    ViewerWidth = MainWindowViewModelHelpers.GetIntPropertyOrDefault(arena, "viewer_width", 760),
                    ViewerHeight = MainWindowViewModelHelpers.GetIntPropertyOrDefault(arena, "viewer_height", 340)
                });
            }

            arenas = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task RefreshSelectedServerArenasAsync(ServerSummaryItem? selectedServer, Action refreshActiveSessionArenaOptions, Action enterArenaViewerContext, bool silent = false)
    {
        if (selectedServer is null)
        {
            ServerArenaEntries.Clear();
            ServerArenasStatus = "Select a server to load active arenas.";
            return;
        }

        var requestedServerId = selectedServer.ServerId;
        var refreshVersion = Interlocked.Increment(ref _serverArenasRefreshVersion);

        if (!silent)
        {
            IsServerArenasLoading = true;
        }

        try
        {
            var knownServer = ToKnownServer(selectedServer);
            var endpointCandidates = ServerServiceViewModel.BuildServerBaseEndpointCandidates(knownServer);

            IReadOnlyList<ServerArenaApiDto>? arenas = null;
            foreach (var endpoint in endpointCandidates)
            {
                var uri = new Uri(endpoint + "/api/arenas");
                try
                {
                    using var response = await _serverCatalogHttpClient.GetAsync(uri);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var payload = await response.Content.ReadAsStringAsync();
                    if (!TryParseArenaList(payload, out var parsed))
                    {
                        continue;
                    }

                    arenas = parsed;
                    break;
                }
                catch
                {
                    // Ignore and try next candidate endpoint.
                }
            }

            if (arenas is null)
            {
                if (!silent && IsArenaRefreshCurrent(refreshVersion, requestedServerId))
                {
                    ServerArenasStatus = "Failed to load active arenas from server.";
                    ServerArenaEntries.Clear();
                }

                ArenaViewerLastError = "Failed to query /api/arenas from all endpoint candidates.";
                return;
            }

            if (!IsArenaRefreshCurrent(refreshVersion, requestedServerId))
            {
                return;
            }

            var uniqueArenas = arenas
                .GroupBy(a => a.ArenaId)
                .Select(g => g.Last())
                .ToList();

            ServerArenaEntries.Clear();
            foreach (var arena in uniqueArenas)
            {
                var arenaId = arena.ArenaId;
                var viewerUrl = arena.ViewerUrl;
                var pluginEntryUrl = arena.PluginEntryUrl;
                var game = arena.Game;

                ServerArenaEntries.Add(new ServerArenaItem(
                    ArenaId: arenaId,
                    Game: string.IsNullOrWhiteSpace(game) ? "unknown" : game,
                    Status: string.IsNullOrWhiteSpace(arena.Status) ? "unknown" : arena.Status,
                    Players: BuildArenaPlayersText(arena.Player1Name, arena.Player2Name),
                    MoveCount: arena.MoveCount,
                    ViewerUrl: viewerUrl,
                    PluginEntryUrl: pluginEntryUrl,
                    ViewerWidth: arena.ViewerWidth,
                    ViewerHeight: arena.ViewerHeight,
                    WatchCommand: new RelayCommand(() => StartWatchingArena(selectedServer, arenaId, game, viewerUrl, pluginEntryUrl, arena.ViewerWidth, arena.ViewerHeight, enterArenaViewerContext))));
            }

            ServerArenasStatus = ServerArenaEntries.Count == 0
                ? "No active arenas were reported by server."
                : $"Active arenas: {ServerArenaEntries.Count}";

            if (!silent)
            {
                refreshActiveSessionArenaOptions();
            }

            if (_watchedArenaId > 0)
            {
                var watched = uniqueArenas.FirstOrDefault(a => a.ArenaId == _watchedArenaId);
                if (watched is not null)
                {
                    UpdateArenaViewerFromArena(watched);
                }
                else
                {
                    ArenaViewerStatus = $"Arena {_watchedArenaId} is no longer active.";
                    ArenaViewerLastError = $"Arena {_watchedArenaId} no longer appears in active arena list.";
                }
            }
        }
        finally
        {
            if (!silent && IsArenaRefreshCurrent(refreshVersion, requestedServerId))
            {
                IsServerArenasLoading = false;
            }
        }
    }

    private bool IsArenaRefreshCurrent(int refreshVersion, string requestedServerId)
    {
        return refreshVersion == _serverArenasRefreshVersion;
    }

    private void StartArenaViewerWatchLoop(Action enterArenaViewerContext)
    {
        StopArenaViewerWatch();

        var cts = new CancellationTokenSource();
        _arenaViewerWatchCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && _watchedArenaId > 0)
            {
                await RefreshSelectedServerArenasAsync(_watchedServer, () => { }, enterArenaViewerContext, silent: true);

                try
                {
                    await Task.Delay(ArenaWatcherPollIntervalMs, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, cts.Token);
    }

    public sealed class ServerArenaApiDto
    {
        public int ArenaId { get; init; }
        public string Game { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Player1Name { get; init; } = string.Empty;
        public string Player2Name { get; init; } = string.Empty;
        public int MoveCount { get; init; }
        public string GameState { get; init; } = string.Empty;
        public string ViewerUrl { get; init; } = string.Empty;
        public string PluginEntryUrl { get; init; } = string.Empty;
        public int ViewerWidth { get; init; } = 760;
        public int ViewerHeight { get; init; } = 340;
    }
}
