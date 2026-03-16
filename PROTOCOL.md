# Build-a-Bot Stadium Protocol v1.0

This document defines the bot protocol for the Build-a-Bot Stadium server. Bot communication is performed over TCP on port 8080, and server responses are sent as JSON objects.

The browser dashboard is not a TCP client. It is served separately over HTTP on port 3000 and receives manager state updates through Server-Sent Events at `/dashboard-sse`.

## Connection Lifecycle

1. **Connect**: Open a TCP connection to the stadium server, for example `localhost:8080`.
2. **Register**: Send `REGISTER <name> <bot_id_or_\"\"> <bot_secret_or_\"\"> [cap1,cap2,...]` immediately.
3. **Interact**: Send commands and read newline-delimited JSON responses.
4. **Disconnect**: The server handles cleanup via `QUIT` or an abrupt disconnect.

---

## Command Reference

| Command | Arguments | Description |
| --- | --- | --- |
| `HELP` | (none) | Returns the available command list for the current session state. |
| `REGISTER` | `<name> <bot_id_or_""> <bot_secret_or_""> [cap1,cap2,...]` | Authenticates with persistent identity. Use `"" ""` to request a new bot identity and secret. |
| `WHOAMI` | (none) | Returns the current session identity and arena status. |
| `UPDATE` | `<field> <value>` | Updates mutable session fields such as name or capabilities. |
| `CREATE` | `<type> <time_ms> <handicap_bool> [args...]` | Creates a new arena for the requested game type. |
| `JOIN` | `<id> <handicap>` | Joins an existing arena by ID. |
| `LIST` | (none) | Returns a human-readable list of currently open arenas. |
| `WATCH` | `<id>` | Enters spectator mode for a match. |
| `MOVE` | `<move>` | Submits a move to the active match. |
| `LEAVE` | (none) | Leaves the current arena. |
| `QUIT` | (none) | Closes the connection to the stadium. |

---

## Response Schema

The server communicates status and game data using a standard JSON structure:

```json
{
  "status": "ok | err",
  "type": "register | create | join | move | info | update | error | timeout | data | list | leave | gameover",
  "payload": "string_or_structured_data"
}
```

`payload` is not restricted to strings. Some responses contain structured data, such as arena summaries or game state.

### Example: Successful Move

Client sends `MOVE 3`

Server responds:

```json
{"status":"ok","type":"move","payload":"accepted"}
```

---

## Gameplay & Enforcement

* **Identity handshake**: A returning bot must supply the same `bot_id` and `bot_secret` it received when first registered. If it sends `""` for both fields, the server issues a new identity.
* **Move timeout**: Moves must be made within the `time_ms` set when the arena was created. If a move exceeds that limit, the arena is finalized and the player receives a `timeout` response.
* **Match history**: Finalized games append a match record that includes participants, moves, outcome, and final game state.
* **Watchdog cleanup**: The server automatically cleans up inactive arenas. `waiting` arenas expire after 1 hour, `active` arenas after 3× their time limit, `completed` arenas after 1 minute, and `aborted` arenas after 5 minutes.

---

## Dashboard Transport

The dashboard is embedded in the server process and is available at `http://localhost:3000`.

* `GET /` — serves the dashboard HTML.
* `GET /dashboard-sse` — streams rendered manager state updates over SSE. Pass `?admin_key=<key>` to unlock admin controls in the UI.
* `POST /admin/eject-bot` — forcefully disconnect a session by ID (admin only).
* `POST /admin/create-arena` — create a new arena from the dashboard (admin only).

Admin access requires the `BBS_DASHBOARD_ADMIN_KEY` environment variable to be set on the server. The admin key is passed as a query parameter or form field; the server verifies it with a constant-time comparison.

There is no separate TCP dashboard client protocol.
