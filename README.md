# Build-a-Bot Stadium

Build-a-Bot Stadium is an extensible bot-arena platform.

It supports a wide game model through dynamically discoverable process-based plugins:

- competitive games
- single-agent environments
- episodic environments

The server runs as one Go process with two surfaces:

- TCP bot server (default `:8080`)
- HTTP dashboard and viewers (default `:3000`)

## Current Runtime Model

The runtime game catalog is sourced from process plugins loaded from manifests (`plugins/games/*.json` by default when plugins are enabled).

The dashboard create-arena forms are populated directly from this live catalog, so new plugin games can appear without changing dashboard code.

## Prerequisites

- Go `1.26.1` or newer

## Run The Server

The dashboard templates are loaded from a relative `templates/` directory, so start from `cmd/bbs-server`:

```bash
cd cmd/bbs-server
go run .
```

Optional custom ports:

```bash
cd cmd/bbs-server
go run . --stadium 18080 --dash 13000
```

Optional dashboard admin mode:

```bash
cd cmd/bbs-server
BBS_DASHBOARD_ADMIN_KEY='mysecretkey' go run .
```

Then open:

- `http://localhost:3000` for normal dashboard mode
- `http://localhost:3000/?admin_key=mysecretkey` for admin mode

## Process Plugin Support

Process plugins are available behind feature flags.

- `BBS_ENABLE_GAME_PLUGINS=true` enables plugin discovery.
- `BBS_GAME_PLUGIN_DIR=/path/to/plugins` overrides plugin directory.
- Default plugin directory is `plugins/games`.

When enabled, the server scans `*.json` manifests in the plugin directory and merges valid plugins into the runtime catalog.

### Minimal Manifest

```json
{
  "protocol_version": 1,
  "name": "counter",
  "display_name": "Counter Plugin",
  "executable": "counter-plugin",
  "viewer_client_entry": "counter_viewer.js",
  "supports_replay": true,
  "supports_move_clock": true,
  "supports_handicap": true,
  "args": [
    {
      "key": "target",
      "label": "Target",
      "input_type": "number",
      "default_value": "10",
      "help": "Winning total."
    }
  ]
}
```

### Sample Plugin Command Included

A reference plugin command is included at `cmd/bbs-game-counter-plugin`.

```bash
go build -o /tmp/bbs-plugin-smoke/counter-plugin ./cmd/bbs-game-counter-plugin
```

Example local plugin run:

```bash
mkdir -p /tmp/bbs-plugin-smoke
# add manifest /tmp/bbs-plugin-smoke/counter.json

cd cmd/bbs-server
BBS_ENABLE_GAME_PLUGINS=true \
BBS_GAME_PLUGIN_DIR=/tmp/bbs-plugin-smoke \
go run .
```

## Plugin Author Quickstart

This section is for developers building new game plugins for BBS.

For a complete field-by-field reference and release checklist, see `PLUGIN_AUTHORING.md`.

### 1. Implement a Plugin Command

Create a Go command similar to `cmd/bbs-game-counter-plugin/main.go` and use `pluginapi.Serve(...)`.

Required gameplay interface (`games/pluginapi.Game`):

- `GetName() string`
- `GetState() string`
- `ValidateMove(playerID int, move string) error`
- `ApplyMove(playerID int, move string) error`
- `IsGameOver() (bool, string)`

Optional interfaces:

- `PlayerCountProvider` for autonomous/one-player environments (`RequiredPlayers() int`, values `0`, `1`, or `2`)
- `MoveClockPolicy` to disable move clock enforcement (`EnforceMoveClock() bool`)
- `HandicapPolicy` to disable handicap controls (`SupportsHandicap() bool`)
- `EpisodicGame` for episodic continuation (`AdvanceEpisode() ...`)

Minimal plugin entrypoint pattern:

```go
func main() {
    if err := pluginapi.Serve(newMyGame); err != nil {
        fmt.Fprintln(os.Stderr, "my plugin error:", err)
        os.Exit(1)
    }
}
```

### 2. Build the Plugin Binary

```bash
go build -o /tmp/bbs-plugin-smoke/my-game-plugin ./cmd/bbs-game-my-plugin
```

### 3. Create a Manifest

Add `/tmp/bbs-plugin-smoke/my-game.json`:

```json
{
  "protocol_version": 1,
  "name": "mygame",
  "display_name": "My Game",
  "executable": "my-game-plugin",
  "viewer_client_entry": "my_game_viewer.js",
  "supports_replay": true,
  "supports_move_clock": true,
  "supports_handicap": true,
  "args": [
    {
      "key": "board_size",
      "label": "Board Size",
      "input_type": "number",
      "default_value": "8",
      "help": "Board edge length."
    }
  ]
}
```

### 4. Enable Plugin Loading

```bash
cd cmd/bbs-server
BBS_ENABLE_GAME_PLUGINS=true \
BBS_GAME_PLUGIN_DIR=/tmp/bbs-plugin-smoke \
go run .
```

### 5. Verify Discovery

- Open dashboard create-arena form and confirm your game appears in dropdown.
- Confirm manifest arg fields render in UI.
- Create arena and execute a few moves.

You can also test via TCP:

```text
CREATE mygame board_size=8
```

### Compatibility Notes

- `protocol_version` must match `games/pluginapi.ProtocolVersion` (currently `1`).
- Plugin process contract uses JSONL requests/responses over stdin/stdout.
- Use stderr for plugin logs; stdout is reserved for protocol messages.
- `viewer_client_entry` is required and must resolve to a JavaScript file in the plugin directory (or an absolute path).
- BBS viewer payloads contain raw frame state; all rendering is handled by the plugin client bundle loaded from `/viewer/plugin-entry?game=<name>`.

### Authoring Best Practices

- Keep `GetState()` deterministic and machine-parseable JSON.
- Return actionable errors from `ValidateMove` and `ApplyMove`.
- Parse args defensively and provide clear validation messages.
- Treat plugin startup and `init` as fast-path operations.
- Avoid hidden global mutable state that can leak across arenas.

### Troubleshooting (Plugin Authors)

- Game not visible in dashboard:
  - verify `BBS_ENABLE_GAME_PLUGINS=true`
  - verify manifest path and `*.json` extension
  - verify `name` is non-empty and unique
- Manifest loads but game creation fails:
  - check executable path in `executable`
  - run plugin binary directly to confirm it starts
  - check server logs for `[game-plugin]` messages
- Manifest skipped during plugin discovery:
  - verify `viewer_client_entry` exists and does not contain `..`
- Plugin appears to hang:
  - ensure protocol writes only JSON responses on stdout
  - ensure logging is written to stderr

## Build

From repository root:

```bash
go build ./...
```

## Bot Protocol Summary

Bots connect over raw TCP and exchange newline-delimited commands and JSON responses.

Typical flow:

1. Connect to `localhost:<stadium_port>`
2. `REGISTER <name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]`
3. Create/join/watch arenas and send moves

Common commands:

- `HELP`
- `REGISTER <name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]`
- `WHOAMI`
- `UPDATE <field> <value>`
- `CREATE <type> [time_ms] [handicap_bool] [args...]`
- `JOIN <arena_id> <handicap_percent>`
- `LIST`
- `WATCH <arena_id>`
- `MOVE <move>`
- `LEAVE`
- `QUIT`

See `PROTOCOL.md` for full details.

## Dashboard

Open `http://localhost:3000`.

The dashboard includes:

- runtime overview and active sessions
- arena cards with live state and viewer links
- archived match replay links
- owner-scoped bot controls via minted owner tokens
- admin controls via `BBS_DASHBOARD_ADMIN_KEY`

Viewer routes:

- `GET /viewer?arena_id=<id>` for live viewer
- `GET /viewer?match_id=<id>` for replay viewer

Data routes:

- `GET /viewer/live-sse?arena_id=<id>`
- `GET /viewer/live-ws?arena_id=<id>`
- `GET /viewer/replay-data?match_id=<id>`
- `GET /viewer/plugin-entry?game=<name>`

## Agent Abstraction (`bbs-agent`)

For bot authors who do not want raw TCP protocol handling:

- `cmd/bbs-agent` bridges BBS TCP to a local JSONL socket
- local bots receive `welcome`/`turn` and send `action`

Docs and templates:

- `BBS_AGENT_CONTRACT.md`
- `cmd/bbs-agent/README.md`
- `examples/python_socket_bot_template.py`

## Quick Manual Test

Start server:

```bash
cd cmd/bbs-server
BBS_ENABLE_GAME_PLUGINS=true go run .
```

Connect bot:

```bash
nc localhost 8080
```

Try commands:

```text
REGISTER bot_one "" "" any
CREATE guess_number max_range=100
LIST
```

## Project Layout

- `cmd/bbs-server/`: TCP server + embedded dashboard/viewers
- `cmd/bbs-agent/`: local bridge for bot authors
- `cmd/bbs-game-counter-plugin/`: reference process plugin command
- `stadium/`: session, arena, manager, snapshots, match history
- `games/`: game interfaces and plugin host/registry
- `games/pluginapi/`: shared process-plugin RPC contract and server helper

All runtime state is currently in memory. Process restart clears sessions, arenas, bot profiles, and match history.

## Security Notes

Current transport is plain TCP and current secrets/token handling is bearer-style.
Treat these as local/home-lab friendly defaults, not internet-hardened production controls.

## Deployment Guides

- Docker on TrueNAS SCALE: `deploy/truenas/README.md`
- No Docker on TrueNAS SCALE: `deploy/truenas-no-docker/README.md`
