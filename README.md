# Build-a-Bot Stadium

Build-a-Bot Stadium is a Go server for running perfect-information bot matches, tracking active arenas, and exposing a lightweight browser dashboard for spectators.

The project currently runs as a single process with two public surfaces:

* A TCP bot server on port `8080`
* An embedded HTTP dashboard on port `3000`

## Current Status

The live game registry currently exposes `connect4`.

The repository contains code for other games and earlier experiments, but only games registered in `games/registry.go` can be created through the running server.

## Prerequisites

* Go `1.26.1` or newer

## Run The Server

The dashboard templates are loaded from a relative `templates/` directory, so the server should be started from `cmd/bbs-server`.

```bash
cd cmd/bbs-server
go run .
```

When the process starts successfully, it brings up:

* The bot server at `localhost:8080`
* The dashboard at `http://localhost:3000`

## Build

From the repository root:

```bash
go build ./...
```

## Bot Protocol Summary

Bots connect over raw TCP and exchange newline-delimited commands and JSON responses.

Typical flow:

1. Connect to `localhost:8080`
2. Send `REGISTER <name> [cap1,cap2,...]`
3. Create, join, watch, or play in arenas

Common commands:

* `HELP`
* `REGISTER <name> [cap1,cap2,...]`
* `WHOAMI`
* `UPDATE <field> <value>`
* `CREATE <type> <time_ms> <handicap_bool> [args...]`
* `JOIN <arena_id> <name> <handicap>`
* `LIST`
* `WATCH <arena_id>`
* `MOVE <move>`
* `LEAVE`
* `QUIT`

For the full wire protocol, see `PROTOCOL.md`.

## Dashboard

Open `http://localhost:3000` in a browser to view active arenas.

The dashboard receives pushed updates over Server-Sent Events from `/arenas-sse`. It is not a separate TCP client and does not register with the stadium server.

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
REGISTER bot_one connect4
CREATE connect4 1000 false
LIST
```

Open `http://localhost:3000` to watch arena updates as they happen.

## Project Layout

* `cmd/bbs-server/`: TCP server and embedded dashboard
* `stadium/`: arena, session, and subscription management
* `games/`: game interfaces and registry
* `games/connect4/`: current live game implementation

Additional design notes live in `ARCHITECTURE.md`.
