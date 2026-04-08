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
    public bool IsServerArenasLoading
    {
        get => _arenaService.IsServerArenasLoading;
        private set => _arenaService.IsServerArenasLoading = value;
    }

    public string ServerArenasStatus
    {
        get => _arenaService.ServerArenasStatus;
        private set => _arenaService.ServerArenasStatus = value;
    }

    public string ArenaViewerLabel
    {
        get => _arenaService.ArenaViewerLabel;
        private set => _arenaService.ArenaViewerLabel = value;
    }

    public string ArenaViewerStatus
    {
        get => _arenaService.ArenaViewerStatus;
        private set => _arenaService.ArenaViewerStatus = value;
    }

    public string ArenaViewerUrl
    {
        get => _arenaService.ArenaViewerUrl;
        private set => _arenaService.ArenaViewerUrl = value;
    }

    public string ArenaViewerPluginEntryUrl
    {
        get => _arenaService.ArenaViewerPluginEntryUrl;
        private set => _arenaService.ArenaViewerPluginEntryUrl = value;
    }

    public string ArenaViewerRawState
    {
        get => _arenaService.ArenaViewerRawState;
        private set => _arenaService.ArenaViewerRawState = value;
    }

    public string ArenaViewerLastUpdatedUtc
    {
        get => _arenaService.ArenaViewerLastUpdatedUtc;
        private set => _arenaService.ArenaViewerLastUpdatedUtc = value;
    }

    public string ArenaViewerLastError
    {
        get => _arenaService.ArenaViewerLastError;
        private set => _arenaService.ArenaViewerLastError = value;
    }

    public double ArenaViewerHostWidth
    {
        get => _arenaService.ArenaViewerHostWidth;
        private set => _arenaService.ArenaViewerHostWidth = value;
    }

    public double ArenaViewerHostHeight
    {
        get => _arenaService.ArenaViewerHostHeight;
        private set => _arenaService.ArenaViewerHostHeight = value;
    }

    public bool IsEmbeddedViewerSupported
    {
        get => _arenaService.IsEmbeddedViewerSupported;
        private set => _arenaService.IsEmbeddedViewerSupported = value;
    }

    public bool ShowEmbeddedViewerFallback => _arenaService.ShowEmbeddedViewerFallback;

    public bool ShowOpenArenaViewerButton => _arenaService.ShowOpenArenaViewerButton;

    public string EmbeddedViewerSupportMessage
    {
        get => _arenaService.EmbeddedViewerSupportMessage;
        private set => _arenaService.EmbeddedViewerSupportMessage = value;
    }

    public string ArenaViewerDiagnostics => _arenaService.ArenaViewerDiagnostics;

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
        _arenaService.RefreshServerArenas(SelectedServer, RefreshActiveSessionArenaOptions, EnterArenaViewerContext);
    }

    private async Task RefreshSelectedServerArenasAsync(bool silent = false)
    {
        _arenaService.RefreshServerArenas(SelectedServer, RefreshActiveSessionArenaOptions, EnterArenaViewerContext);
        await Task.CompletedTask;
    }

    private void EnterArenaViewerContext()
    {
        _uiState.SwitchContext(WorkspaceContext.ArenaViewer);
        RefreshContextProjection();
    }

    private void StartWatchingArena(int arenaId, string game, string viewerUrl, string pluginEntryUrl, int viewerWidth, int viewerHeight)
    {
        _arenaService.StartWatchingArena(SelectedServer, arenaId, game, viewerUrl, pluginEntryUrl, viewerWidth, viewerHeight, EnterArenaViewerContext);
    }

    private void StartArenaViewerWatchLoop()
    {
    }

    private void StopArenaViewerWatch()
    {
        _arenaService.StopArenaViewerWatch();
    }

    private void OpenArenaViewerInBrowser()
    {
        _arenaService.OpenArenaViewerInBrowser();
    }

    private void UpdateArenaViewerFromArena(ArenaServiceViewModel.ServerArenaApiDto arena)
    {
        _arenaService.UpdateArenaViewerFromArena(arena);
    }

    private void ApplyArenaViewerProjection(string viewerUrl, string pluginEntryUrl, int viewerWidth, int viewerHeight, bool overwriteUrlsWhenEmpty)
    {
        _arenaService.ApplyArenaViewerProjection(viewerUrl, pluginEntryUrl, viewerWidth, viewerHeight, overwriteUrlsWhenEmpty);
    }
}
