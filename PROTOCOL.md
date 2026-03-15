# Build-a-Bot Stadium Protocol v1.0

This document defines the bot protocol for the Build-a-Bot Stadium server. Bot communication is performed over TCP on port 8080, and server responses are sent as JSON objects.

The browser dashboard is not a TCP client. It is served separately over HTTP on port 3000 and receives arena list updates through Server-Sent Events at `/arenas-sse`.

## Connection Lifecycle

1. **Connect**: Open a TCP connection to the stadium server, for example `localhost:8080`.
2. **Register**: Send `REGISTER <name> [cap1,cap2,...]` immediately.
3. **Interact**: Send commands and read newline-delimited JSON responses.
4. **Disconnect**: The server handles cleanup via `QUIT` or an abrupt disconnect.

---

## Command Reference

| Command | Arguments | Description |
| --- | --- | --- |
| `HELP` | (none) | Returns the available command list for the current session state. |
| `REGISTER` | `<name> [cap1,cap2,...]` | Authenticates a bot and optionally records supported game capabilities. |
| `WHOAMI` | (none) | Returns the current session identity and arena status. |
| `UPDATE` | `<field> <value>` | Updates mutable session fields such as name or capabilities. |
| `CREATE` | `<type> <time_ms> <handicap_bool> [args...]` | Creates a new arena for the requested game type. |
| `JOIN` | `<id> <name> <handicap>` | Joins an existing arena by ID. |
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
  "type": "register | create | join | move | info | update | error | timeout | data | list | leave",
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

* **Move timeout**: Moves must be made within the `time_ms` set when the arena was created. If a move exceeds that limit, the arena is marked completed and the player receives a `timeout` response.
* **Watchdog cleanup**: The server automatically cleans up inactive arenas. `waiting` arenas expire after 1 hour, `active` arenas after 3x their time limit, and `completed` arenas after 1 minute.

---

## Dashboard Transport

The dashboard is embedded in the server process and is available at `http://localhost:3000`.

* `GET /` serves the dashboard HTML.
* `GET /arenas-sse` streams rendered arena list updates over SSE.

There is no separate TCP dashboard client protocol.
