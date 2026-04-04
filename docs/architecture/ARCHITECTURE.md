# Build-a-Bot Stadium Architecture

This document describes the current architecture of Build-a-Bot Stadium as an extensible arena platform.

The runtime now uses process-based game plugins as the only game source.

## Core Semantics

- `Bot`: any actor attached to a session that can consume stadium JSON protocol output.
- `Arena`: a container that binds one `Game` instance with zero or more sessions.
- `Game`: any plugin that satisfies the contract surface (`games.GameInstance` and related optional interfaces).
- Stadium core: orchestration of sessions, arenas, and contract-compliant plugin instances.

## Runtime Surfaces

A single `cmd/bbs-server` process exposes:

- TCP bot server (`--stadium`, default `:8080`)
- HTTP dashboard and viewers (`--dash`, default `:3000`)

Dashboard surfaces:

- `/` control deck
- `/api/status` explicit dashboard/server liveness acknowledgment
- `/api/game-catalog` explicit runtime game catalog for clients
- `/api/arenas` active arena snapshot for client-side watch workflows
- `/viewer?arena_id=<id>` live arena viewer
- `/viewer/canvas?arena_id=<id>` canvas-only live arena viewer shell
- `/viewer?match_id=<id>` replay viewer

## System Overview

```mermaid
flowchart LR
    Bot[Bot Client]
    Browser[Dashboard Browser]
    Server[cmd/bbs-server]
    Manager[stadium.DefaultManager]
    Registry[games plugin registry]
    PluginHost[Process Plugin Host]
    PluginProc[Game Plugin Process]

    Bot -->|TCP| Server
    Browser -->|HTTP/SSE/WS| Server
    Server --> Manager
    Server --> Registry
    Registry --> PluginHost
    PluginHost --> PluginProc
    Manager -->|snapshots| Browser
```

## Core Components

### TCP Command Surface (`cmd/bbs-server/main.go`)

Responsibilities:

- per-connection session lifecycle
- command parsing and authorization
- registration, create/join/watch/move flows
- uniform JSON envelope emission for all command responses
- disconnect cleanup

### Stadium Manager (`stadium/`)

The manager is the in-memory source of truth.

Responsibilities:

- active sessions and arenas
- bot profiles and match history
- owner-token linkage to live sessions
- arena activation/finalization
- dashboard snapshot publish/subscribe
- watchdog-based cleanup

Concurrency model:

- coarse manager mutex for state maps/counters
- per-session write lock for socket writes
- buffered subscriber channels for dashboard/viewer stream subscribers

### Game Boundary (`games/engine.go`)

Server logic depends on `games.GameInstance` plus optional policy interfaces:

- `RequiredPlayers()` for activation gating on session count
- `EnforceMoveClock()`
- `SupportsHandicap()`
- `AdvanceEpisode()` for episodic environments
- `Close()` (optional cleanup hook for external resources)

### Dynamic Registry (`games/registry.go` + `games/plugin_host.go`)

`GetGame` and `AvailableGameCatalog` resolve from dynamically discovered plugin manifests.

Plugin discovery controls:

- `BBS_ENABLE_GAME_PLUGINS=true`
- `BBS_GAME_PLUGIN_DIR=/path/to/plugins` (default `plugins/games`)

Plugin registry refreshes periodically (currently every 2 seconds), so newly added manifests can appear without server restart in most flows.

## Process Plugin Model

Plugins are separate executables, not `.so` in-process Go plugins.

Benefits:

- crash isolation
- cleaner compatibility story across Go versions
- easier future sandboxing/resource controls
- clearer home-developer distribution model

### Manifest

Each plugin is declared by JSON manifest (`*.json`) in plugin dir.

Key fields:

- `protocol_version`
- `name`
- `display_name`
- `executable`
- `viewer_client_entry` (required)
- `supports_replay`
- `supports_move_clock`
- `supports_handicap`
- `args[]` for dashboard/runtime schema

### RPC Contract (`games/pluginapi`)

Transport:

- JSON lines over plugin process stdin/stdout

Methods:

- `init`
- `get_name`
- `get_state`
- `validate_move`
- `apply_move`
- `is_game_over`
- `advance_episode` (optional)
- `shutdown`

Server-side wrapper:

- `games/plugin_process_game.go` implements `GameInstance` and optional policies by forwarding to RPC.
- Plugin process lifecycle is cleaned up via `games.CloseGame(...)` on arena teardown.

### Plugin Author Workflow (v1)

1. Implement a plugin command that calls `pluginapi.Serve(factory)`.
2. Build a standalone executable for that command.
3. Write a JSON manifest with runtime metadata and arg schema.
4. Place binary + manifest in plugin directory.
5. Enable plugin loading via env vars.
6. Validate discovery in dashboard create-arena dropdown.

The manifest `args` schema is intentionally shared with the dashboard's dynamic arena form renderer so plugin authors can define self-describing configuration inputs without dashboard code changes.

## Viewer Architecture

The viewer uses a client-side rendering model where all game visualization logic runs in the browser.

### Live Viewing

- `/viewer?arena_id=<id>` serves an HTML shell
- `/viewer/canvas?arena_id=<id>` serves a minimal canvas-only HTML shell for embedded clients
- `/viewer/live-sse?arena_id=<id>` (or `/viewer/live-ws`) streams raw game state frames + plugin metadata
- server emits only the raw state string from `game.GetState()`; structured frame data is built by client JS

Embedded client pattern:

- query `/api/arenas` for active arenas and preferred `viewer_url`
- use `viewer_width`/`viewer_height` hints to size host surfaces without stretching
- open `viewer_url` (currently canvas-only shell) in embedded WebView

### Replay Viewing

- `/viewer?match_id=<id>` serves an HTML shell for archived matches
- `/viewer/replay-data?match_id=<id>` returns a JSON object with frame history (downsampled by `max_frames` query param)
- client reconstructs frames by parsing raw state and applying game plugin logic

### Plugin Client Entry Point

- Each plugin manifest declares `viewer_client_entry`: a relative or absolute path to a JavaScript file
- `/viewer/plugin-entry?game=<name>` serves the JS bundle for a specific game
- Client runtime calls `window.BBSViewerPluginRuntime.register(gameName, { render: ... })` to install the renderer
- Renderer receives raw state and renders to a canvas

This model ensures:
- zero server-side render coupling
- game developers own visualization entirely
- replay viewing works identically to live viewing
- viewer bundle is versioned with the game plugin

## Dashboard Integration

Dashboard create-arena forms are generated from `AvailableGameCatalog()`.

Effects:

- dropdown is always in sync with runtime game catalog
- per-game arg fields are schema-driven
- move-clock/handicap controls auto-adjust by game capability
- viewer pages load plugin-provided client bundles via `/viewer/plugin-entry`

This means valid plugins are presented uniformly in owner and admin create flows.

## Arena Lifecycle

1. Game selected (dashboard or TCP `CREATE` command)
2. `games.GetGame(type, args)` builds game instance
3. Manager creates arena with policy flags and required player count
4. Players/spectators attach
5. Moves flow through `ValidateMove`/`ApplyMove`
6. On terminal outcome, match record is archived
7. Arena cleanup calls `games.CloseGame` for optional resource shutdown

## Current Boundaries

- all manager state remains in-memory
- plugin trust model is currently local/trusted filesystem
- plugin health/version attestation is not yet enforced
- transport security is still plain TCP for bot channel

These are known next-hardening areas, not architectural blockers.
