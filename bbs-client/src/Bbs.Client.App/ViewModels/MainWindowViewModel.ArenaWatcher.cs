using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int ArenaWatcherPollIntervalMs = 900;

    private CancellationTokenSource? _arenaViewerWatchCts;
    private int _watchedArenaId;
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

    public bool IsServerArenasLoading
    {
        get => _isServerArenasLoading;
        private set
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
        private set
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
        private set
        {
            if (_arenaViewerLabel == value)
            {
                return;
            }

            _arenaViewerLabel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTitleText));
        }
    }

    public string ArenaViewerStatus
    {
        get => _arenaViewerStatus;
        private set
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
        private set
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
        private set
        {
            if (_arenaViewerUrl == value)
            {
                return;
            }

            _arenaViewerUrl = value;
            OnPropertyChanged();
            ((RelayCommand)OpenArenaViewerInBrowserCommand).RaiseCanExecuteChanged();
        }
    }

    public string ArenaViewerPluginEntryUrl
    {
        get => _arenaViewerPluginEntryUrl;
        private set
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
        private set
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
        private set
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
        private set
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
        private set
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
        private set
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
        private set
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

    internal void ConfigureEmbeddedViewerSupport(bool isAvailable, string? message)
    {
        IsEmbeddedViewerSupported = isAvailable;
        EmbeddedViewerSupportMessage = string.IsNullOrWhiteSpace(message)
            ? (isAvailable ? "Embedded JS viewer is available." : "Embedded JS viewer is unavailable; using fallback mode.")
            : message.Trim();
    }

    internal void RegisterEmbeddedViewerRuntimeFailure(string reason)
    {
        IsEmbeddedViewerSupported = false;
        EmbeddedViewerSupportMessage = string.IsNullOrWhiteSpace(reason)
            ? "Embedded JS viewer failed to initialize; using fallback mode."
            : reason.Trim();
    }

    private void RefreshServerArenas()
    {
        _ = RefreshSelectedServerArenasAsync();
    }

    private async Task RefreshSelectedServerArenasAsync(bool silent = false)
    {
        var server = SelectedServer;
        if (server is null)
        {
            ServerArenaEntries.Clear();
            ServerArenasStatus = "Select a server to load active arenas.";
            return;
        }

        if (!silent)
        {
            IsServerArenasLoading = true;
        }

        try
        {
            var knownServer = ToKnownServer(server);
            var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);

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
                if (!silent)
                {
                    ServerArenasStatus = "Failed to load active arenas from server.";
                }

                ArenaViewerLastError = "Failed to query /api/arenas from all endpoint candidates.";

                return;
            }

            ServerArenaEntries.Clear();
            foreach (var arena in arenas)
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
                    WatchCommand: new RelayCommand(() => StartWatchingArena(arenaId, game, viewerUrl, pluginEntryUrl, arena.ViewerWidth, arena.ViewerHeight))));
            }

            ServerArenasStatus = ServerArenaEntries.Count == 0
                ? "No active arenas were reported by server."
                : $"Active arenas: {ServerArenaEntries.Count}";

            if (!silent)
            {
                RefreshActiveSessionArenaOptions();
            }

            if (_watchedArenaId > 0)
            {
                var watched = arenas.FirstOrDefault(a => a.ArenaId == _watchedArenaId);
                if (watched is not null)
                {
                    UpdateArenaViewerFromArena(watched);
                }
                else if (_currentContext == WorkspaceContext.ArenaViewer)
                {
                    ArenaViewerStatus = $"Arena {_watchedArenaId} is no longer active.";
                    ArenaViewerLastError = $"Arena {_watchedArenaId} no longer appears in active arena list.";
                }
            }
        }
        finally
        {
            if (!silent)
            {
                IsServerArenasLoading = false;
            }
        }
    }

    private void StartWatchingArena(int arenaId, string game, string viewerUrl, string pluginEntryUrl, int viewerWidth, int viewerHeight)
    {
        _watchedArenaId = arenaId;
        ArenaViewerLabel = $"Arena #{arenaId} ({(string.IsNullOrWhiteSpace(game) ? "unknown" : game)})";
        ArenaViewerUrl = viewerUrl;
        ArenaViewerPluginEntryUrl = pluginEntryUrl;
        ArenaViewerHostWidth = Math.Max(300, viewerWidth > 0 ? viewerWidth : 760);
        ArenaViewerHostHeight = Math.Max(200, viewerHeight > 0 ? viewerHeight : 340);
        ArenaViewerStatus = "Starting live arena watch...";
        ArenaViewerRawState = string.Empty;
        ArenaViewerLastError = string.Empty;

        _currentContext = WorkspaceContext.ArenaViewer;
        RefreshContextProjection();

        if (!IsEmbeddedViewerSupported)
        {
            ArenaViewerStatus = "Embedded viewer unavailable; using fallback mode with raw state and external viewer link.";
        }

        StartArenaViewerWatchLoop();
    }

    private void StartArenaViewerWatchLoop()
    {
        StopArenaViewerWatch();

        var cts = new CancellationTokenSource();
        _arenaViewerWatchCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && _watchedArenaId > 0)
            {
                await RefreshSelectedServerArenasAsync(silent: true);

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

    private void StopArenaViewerWatch()
    {
        var cts = _arenaViewerWatchCts;
        _arenaViewerWatchCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void OpenArenaViewerInBrowser()
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

    private void UpdateArenaViewerFromArena(ServerArenaApiDto arena)
    {
        ArenaViewerStatus = $"Arena {arena.ArenaId} | {arena.Status} | Moves: {arena.MoveCount}";
        ArenaViewerRawState = string.IsNullOrWhiteSpace(arena.GameState)
            ? "(no game state reported)"
            : arena.GameState;
        ArenaViewerLastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
        ArenaViewerLastError = string.Empty;

        if (!string.IsNullOrWhiteSpace(arena.ViewerUrl))
        {
            ArenaViewerUrl = arena.ViewerUrl;
        }

        if (!string.IsNullOrWhiteSpace(arena.PluginEntryUrl))
        {
            ArenaViewerPluginEntryUrl = arena.PluginEntryUrl;
        }

        ArenaViewerHostWidth = Math.Max(300, arena.ViewerWidth > 0 ? arena.ViewerWidth : 760);
        ArenaViewerHostHeight = Math.Max(200, arena.ViewerHeight > 0 ? arena.ViewerHeight : 340);
    }

    private static bool TryParseArenaList(string payload, out IReadOnlyList<ServerArenaApiDto> arenas)
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

    private static string BuildArenaPlayersText(string player1Name, string player2Name)
    {
        var p1 = string.IsNullOrWhiteSpace(player1Name) ? "-" : player1Name;
        var p2 = string.IsNullOrWhiteSpace(player2Name) ? "-" : player2Name;
        return $"{p1} vs {p2}";
    }

    private static Bbs.Client.Core.Domain.KnownServer ToKnownServer(ServerSummaryItem server)
    {
        return Bbs.Client.Core.Domain.KnownServer.Create(
            serverId: server.ServerId,
            name: server.Name,
            host: server.Host,
            port: server.Port,
            useTls: server.UseTls,
            metadata: new Dictionary<string, string>(server.Metadata),
            createdAtUtc: server.CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed class ServerArenaApiDto
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
