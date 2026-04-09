# Build-a-Bot Stadium Protocol v1.2

This document defines the TCP bot protocol for Build-a-Bot Stadium.

The protocol is domain-agnostic and models session/arena interactions over a contract-compliant plugin instance.
The runtime catalog is plugin-driven and discovered from manifests.

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
| `REGISTER` | `<name> [cap1,cap2,...] [owner_token=<token>] [client_nonce=<nonce>] [client_ts=<ts>]` | Registers a runtime session. `owner_token` links dashboard owner controls; nonce/timestamp fields support handshake proof binding. |
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

- Registration is session-based: server issues `session_id` and marks the connection as registered.
- `bot_id`/`bot_secret` are no longer part of REGISTER authentication.
- `owner_token=<token>` links session to dashboard owner controls.

On successful `REGISTER`, payload includes server-issued access metadata:

- `owner_token`: owner credential linked to the active session (issued when omitted by client)
- `dashboard_host`: host the dashboard is reachable on for this server
- `dashboard_port`: dashboard HTTP port
- `dashboard_endpoint`: `host:port` convenience field

Desktop clients may mirror this metadata onto the selected known server record so server-context actions can be rehydrated after reload without requiring an attached bot session to be the only source of truth.

Owner token linkage is single-session: one active bot can claim a given token at a time.

## Timing, Handicap, And Policies

- Move clock enforcement depends on game policy (`EnforceMoveClock()`).
- Handicap support depends on game policy (`SupportsHandicap()`).
- Effective move time is derived from base time and handicap percent.

For games/environments that disable move clocks, time and handicap inputs are ignored.

## Arena Types

Arena interaction patterns are defined by plugin policy and required session count.

Common patterns:

- multi-session arenas
- autonomous arenas (`RequiredPlayers() == 0`)
- single-session arenas (`RequiredPlayers() == 1`)

Episodic environments can continue across terminal episodes when supported by game implementation.

## Dashboard/Viewer HTTP Endpoints

The dashboard is served on default port `3000` (override with `--dash`).

- `GET /` dashboard
- `GET /dashboard-sse` live state stream
- `GET /dashboard-ws` live state stream (websocket)
- `GET /api/status` liveness acknowledgment (`{"status":"ok"}`)
- `GET /api/game-catalog` runtime game/plugin catalog JSON
- `GET /api/arenas` active arena list for clients and automation
- `GET /viewer?arena_id=<id>` live viewer shell
- `GET /viewer/canvas?arena_id=<id>` canvas-only live viewer shell (used by embedded clients)
- `GET /viewer/live-sse?arena_id=<id>` live viewer stream
- `GET /viewer/live-ws?arena_id=<id>` live viewer stream (websocket)
- `GET /viewer?match_id=<id>` replay shell
- `GET /viewer/replay-data?match_id=<id>` replay JSON
- `GET /viewer/plugin-entry?game=<name>` plugin viewer client bundle

Viewer rendering contract (current release):

- Live/replay frame payloads include `raw_state` from plugin `GetState()` output.
- Rendering is frame-stream first: if `raw_state.viewer.frame_stream` is present and valid, viewers render pixels directly.
- JS renderer remains a compatibility fallback loaded via `/viewer/plugin-entry?game=<name>` when frame stream is absent/invalid.
- `viewer_client_entry` remains required by current manifest/linter/runtime validation, even when plugins publish frame-stream payloads.
- JS-only rendering is planned for deprecation in a future release.

`viewer.frame_stream` packet fields:

- Required: `mime_type`, `encoding` (`base64` | `utf8` | `data_url`), `data`
- Optional: `version`, `width`, `height`, `frame_id`, `key_frame`

`GET /api/arenas` response includes per-arena viewer hints:

- `viewer_url`: preferred live viewer URL (currently `/viewer/canvas?...`)
- `plugin_entry_url`: resolved plugin JS bundle URL (when available)
- `viewer_width`, `viewer_height`: native render dimensions for embedded client sizing

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
