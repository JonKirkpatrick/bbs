# BBS Client (Track A)

This directory contains the Avalonia desktop client scaffold for Track A.

## Project Structure

- `src/Bbs.Client.App`: Avalonia desktop app shell and view models
- `src/Bbs.Client.Core`: shared contracts and core abstractions
- `src/Bbs.Client.Infrastructure`: local infrastructure implementations (logging, paths)
- `Bbs.Client.sln`: solution file

## Build

```bash
dotnet build bbs-client/Bbs.Client.sln
```

## Run

```bash
dotnet run --project bbs-client/src/Bbs.Client.App/Bbs.Client.App.csproj
```

## Logs

Structured local logs are written to:

- `$XDG_STATE_HOME/bbs-client/logs/client.log` when `XDG_STATE_HOME` is set
- `~/.local/state/bbs-client/logs/client.log` otherwise

## Storage

SQLite storage is initialized automatically on startup at:

- `$XDG_STATE_HOME/bbs-client/data/client.db` when `XDG_STATE_HOME` is set
- `~/.local/state/bbs-client/data/client.db` otherwise

## Current UI Shell

The app currently launches into a unified workspace shell with:

- Left panel: bot cards
- Center panel: activity workspace host
- Right panel: server cards
- Left and right panel collapse/expand toggles with centered panel headers

Bot registration/edit is available in the center workspace, and saved bot profiles persist in SQLite and reload into the left panel on startup.

Arm/disarm baseline controls are available in the center bot workspace. Lifecycle state transitions are persisted in SQLite and reflected in left-panel bot card status text.

Current UX refinements in this shell include:

- Card-focused selection and hover treatment in side panels
- Keyboard focus-visible styling for side-panel list navigation
- Bottom-row full-width `New Bot` action on the left panel
- Mirrored disabled `New Server` placeholder action on the right panel for future server registration flow

Arena watch integration now supports embedded plugin rendering from server context:

- Server discovery and catalog are API-first (`/api/status`, `/api/game-catalog`)
- Active arena list comes from `/api/arenas`
- Embedded viewer loads the server-provided `viewer_url` (canvas-only shell)
- Host sizing uses server-provided native viewer dimensions to avoid aspect distortion

Linux note:

- Embedded WebView is opt-in by default on Linux
- Set `BBS_ENABLE_EMBEDDED_WEBVIEW=1` to enable in-app plugin rendering
