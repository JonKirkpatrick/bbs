# Build-a-Bot Stadium Protocol v1.1

This document defines the bot protocol for the Build-a-Bot Stadium server. Bot communication is performed over TCP (default port `8080`, configurable at launch with `--stadium`), and server responses are sent as JSON objects.

The browser dashboard is not a TCP client. It is served separately over HTTP (default port `3000`, configurable at launch with `--dash`) and receives manager state updates through Server-Sent Events at `/dashboard-sse`.

## Connection Lifecycle

1. **Connect**: Open a TCP connection to the stadium server, for example `localhost:8080` (or your configured `--stadium` port).
2. **Register**: Send `REGISTER <name> <bot_id_or_\"\"> <bot_secret_or_\"\"> [cap1,cap2,...] [owner_token=<token>]` immediately.
3. **Interact**: Send commands and read newline-delimited JSON responses.
4. **Disconnect**: The server handles cleanup via `QUIT` or an abrupt disconnect.

---

## Command Reference

| Command | Arguments | Description |
| --- | --- | --- |
| `HELP` | (none) | Returns the available command list for the current session state. |
| `REGISTER` | `<name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...] [owner_token=<token>]` | Authenticates with persistent identity. Use `"" ""` to request a new bot identity and secret. If an `owner_token` is included, the active session is linked to the dashboard view that minted it. |
| `WHOAMI` | (none) | Returns the current session identity and arena status. |
| `UPDATE` | `<field> <value>` | Updates mutable session fields such as name or capabilities. |
| `CREATE` | `<type> [time_ms] [handicap_bool] [args...]` | Creates a new arena for the requested game type. `time_ms` and `handicap_bool` are optional; defaults are applied when omitted. Example for gridworld: `CREATE gridworld map=default [map_dir=...] [max_steps=...] [episodes=...]`. |
| `JOIN` | `<id> <handicap_percent>` | Joins an existing arena by ID with a handicap percentage (`+20` gives 20% more move time, `-20` gives 20% less). |
| `LIST` | (none) | Returns a human-readable list of currently open arenas. |
| `WATCH` | `<arena_id>` | Enters spectator mode for a live arena. This is separate from the HTTP replay viewer used for archived matches. |
| `MOVE` | `<move>` | Submits a move to the active match. |
| `LEAVE` | (none) | Leaves the current arena. |
| `QUIT` | (none) | Closes the connection to the stadium. |

---

## Response Schema

The server communicates status and game data using a standard JSON structure:

```json
{
  "status": "ok | err",
  "type": "register | auth | create | join | move | info | update | error | timeout | data | list | leave | gameover | ejected",
  "payload": "string_or_structured_data"
}
```

`payload` is not restricted to strings. Some responses contain structured data, such as arena summaries or game state.

Most command responses are JSON, but a few legacy command paths still write plain text directly to the socket, notably parts of `HELP`, `WATCH`, `QUIT`, and unknown-command handling.

### Example: Successful Move

Client sends `MOVE 3`

Server responds:

```json
{"status":"ok","type":"move","payload":"accepted"}
```

---

## Gameplay & Enforcement

* **Identity handshake**: A returning bot must supply the same `bot_id` and `bot_secret` it received when first registered. If it sends `""` for both fields, the server issues a new identity.
* **Secrets are stored in memory**: The current implementation keeps `bot_secret` values in memory as part of `BotProfile`. There is no hashing, persistence layer, or challenge-response handshake yet.
* **Dashboard claim token**: The browser dashboard can mint an `owner_token`. Include `owner_token=<token>` in `REGISTER` to bind that live session to the dashboard view and unlock owner-scoped controls there. A token can be linked to only one active session at a time.
* **Arena attachment rule**: A session cannot join a second arena while already attached to one. It must leave or be finalized first.
* **Move timeout**: Moves must be made within the player's effective move limit. Effective limit is derived from the arena `time_ms` and the player's `handicap_percent` (`effective = base * (100 + handicap) / 100`).
* **Handicap range**: Handicap is only accepted when the arena was created with `handicap_bool=true`, and must be between `-90` and `300`.
* **Match history**: Finalized games append a match record that includes participants, moves, outcome, and final game state.
* **Watchdog cleanup**: The server automatically cleans up inactive arenas. `waiting` arenas expire after 1 hour, `active` arenas after 3× their time limit, `completed` arenas after 1 minute, and `aborted` arenas after 5 minutes.

---

## Dashboard Transport

The dashboard is embedded in the server process and is available at `http://localhost:3000` by default (or your configured `--dash` port).

* `GET /` — serves the dashboard HTML.
* `GET /dashboard-sse` — streams rendered manager state updates over SSE. Pass `?admin_key=<key>` to unlock admin controls in the UI and `?owner_token=<token>` to show owner-scoped controls for a claimed bot.
* `GET /viewer?arena_id=<id>` — opens the live arena viewer UI.
* `GET /viewer?match_id=<id>` — opens the replay viewer UI for an archived match.
* `GET /viewer/live-sse?arena_id=<id>` — streams viewer frame updates for a live arena.
* `GET /viewer/replay-data?match_id=<id>` — returns replay frames and viewer metadata as JSON.
* `POST /owner/register-bot` — mint a new dashboard owner token and reload the page with it.
* `POST /owner/create-arena` — create a new arena if the current owner token is linked to an active bot session.
* `POST /owner/join-arena` — join an open arena with the bot linked to the current owner token.
* `POST /owner/leave-arena` — remove the linked bot from its current arena without closing the connection. The bot stays registered and can join another arena immediately.
* `POST /owner/eject-bot` — disconnect the bot linked to the current owner token.
* `POST /admin/eject-bot` — forcefully disconnect a session by ID (admin only).
* `POST /admin/create-arena` — create a new arena from the dashboard (admin only).
* `POST /admin/leave-arena` — remove any session from its current arena by session ID without closing the connection (admin only).
* `POST /admin/join-arena` — join any registered session to an arena by session ID (admin only).

Admin access requires the `BBS_DASHBOARD_ADMIN_KEY` environment variable to be set on the server. The admin key is passed as a query parameter or form field; the server verifies it with a constant-time comparison.

There is no separate TCP dashboard client protocol.
