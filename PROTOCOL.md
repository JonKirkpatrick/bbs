# Build-a-Bot Stadium Protocol v1.2

This document defines the TCP bot protocol for Build-a-Bot Stadium.

The platform supports both competitive games and environment-style arenas through process-based plugins exposed under the same command model.
The runtime game catalog is plugin-driven and discovered from manifests.

Plugin author note: process plugin RPC is a separate contract from this TCP bot protocol. See `games/pluginapi/protocol.go` and `README.md` Plugin Author Quickstart.

## Transport

- Protocol: TCP
- Default stadium port: `8080` (override with `--stadium`)
- Framing: newline-delimited commands in, newline-delimited JSON responses out

Server greeting is also JSONL: on connect, the server emits a `welcome` envelope.

The dashboard is HTTP with SSE/WebSocket streams and is not a TCP client.

## Connection Lifecycle

1. Connect to server (`host:stadium_port`)
2. Send `REGISTER ...`
3. Send gameplay commands
4. Disconnect with `QUIT` or socket close

## Command Reference

| Command | Arguments | Description |
| --- | --- | --- |
| `HELP` | (none) | Returns available commands for current session state. |
| `REGISTER` | `<name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]` | Registers a session. Use `"" ""` to request new identity credentials. |
| `WHOAMI` | (none) | Returns session identity and arena linkage state. |
| `UPDATE` | `<field> <value>` | Updates mutable session metadata (for example name/capabilities). |
| `CREATE` | `<type> [time_ms] [handicap_bool] [args...]` | Creates an arena. `type` must exist in runtime plugin catalog. |
| `JOIN` | `<arena_id> <handicap_percent>` | Joins arena as player with handicap adjustment. |
| `LIST` | (none) | Returns currently open arenas. |
| `WATCH` | `<arena_id>` | Observes a live arena over TCP updates. |
| `MOVE` | `<move>` | Submits move/action for current arena. |
| `LEAVE` | (none) | Leaves current arena while staying connected. |
| `QUIT` | (none) | Disconnects session. |

## CREATE Semantics

`CREATE` accepts dynamic game args after optional runtime options.

General form:

```text
CREATE <type> [time_ms] [handicap_bool] [args...]
```

Examples:

```text
CREATE mygame 1000 false board_size=8
CREATE counter target=15
```

`<type>` is resolved by `games.GetGame` against plugin manifests (when `BBS_ENABLE_GAME_PLUGINS=true`).

## Response Schema

All server responses (including command replies and connection greeting) use one envelope:

```json
{
  "status": "ok | err",
  "type": "welcome | help | register | auth | create | join | watch | move | info | update | error | timeout | data | list | leave | quit | gameover | episode_end | ejected",
  "payload": "string_or_structured_data"
}
```

`payload` may be string, object, or array.

## Identity And Ownership

- Returning bots must use matching `bot_id` + `bot_secret`.
- New bots send `""` for both to request issuance.
- `owner_token=<token>` links session to dashboard owner controls.

Owner token linkage is single-session: one active bot can claim a given token at a time.

## Timing, Handicap, And Policies

- Move clock enforcement depends on game policy (`EnforceMoveClock()`).
- Handicap support depends on game policy (`SupportsHandicap()`).
- Effective move time is derived from base time and handicap percent.

For games/environments that disable move clocks, time and handicap inputs are ignored.

## Arena Types

The platform supports both:

- two-player arenas
- zero-player arenas/environments (`RequiredPlayers() == 0`)
- one-player arenas/environments (`RequiredPlayers() == 1`)

Episodic environments can continue across terminal episodes when supported by game implementation.

## Dashboard/Viewer HTTP Endpoints

The dashboard is served on default port `3000` (override with `--dash`).

- `GET /` dashboard
- `GET /dashboard-sse` live state stream
- `GET /dashboard-ws` live state stream (websocket)
- `GET /viewer?arena_id=<id>` live viewer shell
- `GET /viewer/live-sse?arena_id=<id>` live viewer stream
- `GET /viewer/live-ws?arena_id=<id>` live viewer stream (websocket)
- `GET /viewer?match_id=<id>` replay shell
- `GET /viewer/replay-data?match_id=<id>` replay JSON
- `GET /viewer/plugin-entry?game=<name>` plugin viewer client bundle

Owner actions:

- `POST /owner/register-bot`
- `POST /owner/create-arena`
- `POST /owner/join-arena`
- `POST /owner/leave-arena`
- `POST /owner/eject-bot`

Admin actions (requires `BBS_DASHBOARD_ADMIN_KEY`):

- `POST /admin/create-arena`
- `POST /admin/join-arena`
- `POST /admin/leave-arena`
- `POST /admin/eject-bot`

## Security Notes

Current bot transport is plain TCP and current identity/ownership controls are bearer-style.
Treat this as local/home-lab baseline unless additional network security layers are applied.
