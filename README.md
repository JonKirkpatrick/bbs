# Build-a-Bot Stadium

Build-a-Bot Stadium is a Go server for running perfect-information bot matches, tracking active arenas, and exposing a browser dashboard for spectators, admins, and bot owners.

The project currently runs as a single process with two public surfaces:

* A TCP bot server on port `8080` by default
* An embedded HTTP dashboard on port `3000` by default

## Current Status

The live game registry currently exposes `connect4` and `gridworld`.

The repository contains code for other games and earlier experiments, but only games registered in `games/registry.go` can be created through the running server.

## Prerequisites

* Go `1.26.1` or newer

## Run The Server

The dashboard templates are loaded from a relative `templates/` directory, so the server should be started from `cmd/bbs-server`.

```bash
cd cmd/bbs-server
go run .
```

Optional custom ports at launch:

```bash
cd cmd/bbs-server
go run . --stadium 18080 --dash 13000
```

When the process starts successfully, it brings up:

* The bot server at `localhost:8080` (or your `--stadium` value)
* The dashboard at `http://localhost:3000` (or your `--dash` value)

### Dashboard Admin Mode

Set `BBS_DASHBOARD_ADMIN_KEY` to enable admin controls in the dashboard (eject sessions, create arenas, forcibly move sessions between arenas):

```bash
BBS_DASHBOARD_ADMIN_KEY='mysecretkey' go run . --stadium 8080 --dash 3000
```

Then open `http://localhost:<dash_port>/?admin_key=mysecretkey` (default `3000`). Use single quotes around keys that contain shell-special characters.

### Dashboard Bot Control Mode

The dashboard also exposes a public Register Bot flow. Clicking Register Bot mints an owner token for the current browser view.

The control panel then shows:

* the TCP host/IP the bot should connect to
* the bot TCP port
* the owner token to include in `REGISTER`

Once a live bot session registers with that token, the dashboard unlocks owner-scoped actions for that bot:

* create an arena
* join an open arena
* leave the current arena (without disconnecting)
* disconnect the bot

This is separate from admin mode. Admin mode can operate on any session. Owner mode is limited to the session currently linked to the minted token.

## Build

From the repository root:

```bash
go build ./...
```

## Bot Protocol Summary

Bots connect over raw TCP and exchange newline-delimited commands and JSON responses.

Typical flow:

1. Connect to `localhost:<stadium_port>` (default `8080`)
2. Send `REGISTER <name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]`
3. Create, join, watch, or play in arenas

If a bot has no identity yet, it sends `""` for both `bot_id` and `bot_secret` to request a new identity pair.

Common commands:

* `HELP`
* `REGISTER <name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]`
* `WHOAMI`
* `UPDATE <field> <value>`
* `CREATE <type> [time_ms] [handicap_bool] [args...]`
* `JOIN <arena_id> <handicap_percent>`
* `LIST`
* `WATCH <arena_id>`
* `MOVE <move>`
* `LEAVE`
* `QUIT`

For the full wire protocol, see `PROTOCOL.md`.

## Agent Abstraction

If you want bot authors to avoid handling raw TCP and BBS JSON directly, see:

* `BBS_AGENT_CONTRACT.md` - local bridge protocol (`bbs-agent` <-> bot over Unix socket JSONL)
* `examples/python_socket_bot_template.py` - minimal socket bot template for bridge mode

The intended model is:

1. `bbs-agent` handles server networking/registration/reconnect
2. bot code connects locally and handles decision logic (`turn` in, `action` out)

### bbs-agent MVP Command

From repository root:

```bash
go run ./cmd/bbs-agent \
	--server localhost:8080 \
	--listen /tmp/bbs-agent.sock
```

Then connect a local bot:

```bash
python3 examples/python_socket_bot_template.py \
	--socket /tmp/bbs-agent.sock \
	--name agent_python_bot \
	--owner-token owner_...
```

See `cmd/bbs-agent/README.md` for details.

### Fhourstones Worker Example

If you want to drive moves through the imported Fhourstones solver:

Fhourstones native local-bridge adapter is planned as part of the migration.
Solver assets remain in `examples/Fhourstones/README.md`.

## Dashboard

Open `http://localhost:3000` in a browser to view active arenas. The dashboard shows live session state, arena state, a persistent bot registry, recent match records, live and replay viewer links, and owner/admin action panels.

If you launched with `--dash`, replace `3000` with your configured dashboard port.

The dashboard receives pushed updates over Server-Sent Events from `/dashboard-sse`. It is not a separate TCP client and does not register with the stadium server.

The match viewer is served at `/viewer`:

* `GET /viewer?arena_id=<id>` opens a live arena viewer
* `GET /viewer?match_id=<id>` opens a replay viewer for an archived match

The replay viewer loads frame data from `/viewer/replay-data`, and the live viewer streams frame updates over `/viewer/live-sse`.

For an always-on home-lab deployment, see `deploy/truenas/README.md` (Docker) or `deploy/truenas-no-docker/README.md` (no Docker).
Those guides use `/mnt/<pool>/apps` placeholders; replace `<pool>` with your dataset path (for example, `/mnt/DeepEnd/apps`).

## Quick Manual Test

Start the server in one terminal:

```bash
cd cmd/bbs-server
go run .
```

Connect a bot in another terminal:

```bash
nc localhost 8080
```

Then send commands like:

```text
REGISTER bot_one "" "" connect4
CREATE connect4 1000 false
LIST
```

Create a gridworld arena from the default map:

```text
CREATE gridworld map=default episodes=25
```

You can also point at a custom map directory:

```text
CREATE gridworld map=maze_a map_dir=../../maps/gridworld max_steps=80 episodes=0
```

For gridworld, `episodes` controls how many terminal episodes run inside the same arena:

* `episodes=0` means unbounded episodic loop (arena remains active until players leave or admin cleanup)
* `episodes=N` runs exactly `N` episodes, then emits normal `gameover`

Gridworld also disables per-move clock enforcement, so time and handicap settings are ignored for gridworld arenas.

Join an existing live arena from a second bot:

```text
REGISTER bot_two "" "" connect4
JOIN 1 0
```

Watch a live arena without playing:

```text
REGISTER spectator "" ""
WATCH 1
```

Or claim dashboard controls with a token minted from the browser:

```text
REGISTER bot_one "" "" connect4 owner_token=owner_...
```

Open `http://localhost:3000` to watch arena updates as they happen.

If you launched with `--dash`, replace `3000` with your configured dashboard port.

## Project Layout

* `cmd/bbs-server/`: TCP server and embedded dashboard
* `cmd/bbs-agent/`: sidecar bridge that abstracts BBS TCP from bot authors
* `stadium/`: arena, session, manager, and subscription management
* `games/`: game interfaces and registry
* `games/connect4/`: current live game implementation

The stadium package also owns persistent bot profiles (`BotProfile`), match records (`MatchRecord`), and per-move history (`MatchMove`). All state is currently in-memory; a restart discards everything.

Additional design notes live in `ARCHITECTURE.md`.

## GridWorld Maps

Gridworld map files are loaded at runtime from:

1. `map_dir=...` in `CREATE`
2. `BBS_GRIDWORLD_MAP_DIR`
3. defaults: `maps/gridworld`, `../maps/gridworld`, `../../maps/gridworld`

Map format is plain text with metadata, blank line, then integer grid values.

Example (`maps/gridworld/default.map`):

```text
name: default
rows: 6
cols: 8
max_steps: 64

2 0 0 0 1 0 0 3
1 1 1 0 1 0 1 0
...
```

Cell codes:

* `0` empty
* `1` wall
* `2` start
* `3` goal (win)
* `4` pit (loss)

Supported `CREATE` args for gridworld:

* `map=<name>` map basename (`default` => `default.map`)
* `map_dir=<path>` map directory override
* `max_steps=<n>` step horizon for each episode
* `episodes=<n>` number of episodes in this arena (`0` means unbounded)
