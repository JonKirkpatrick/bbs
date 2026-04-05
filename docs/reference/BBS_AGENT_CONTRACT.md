# BBS Agent Contract v0.2 (Local Bridge)

This contract defines local JSONL protocols between:

- `bbs-agent` (bridge to BBS TCP server)
- a local bot process (usually over Unix socket on linux/mac)
- a local client process over control socket (for `bbs-client` and other tooling)

`bbs-agent` does not launch bot subprocesses. Bot authors connect to the endpoint exposed by `--listen`.
Control clients connect to `--control-listen` (or the default derived control endpoint).

## Purpose

The bridge keeps server/network details inside `bbs-agent` so bot code can focus on decisions.

This applies to any contract-compliant arena interaction pattern.

## Design

- Agent owns BBS networking, registration, reconnection behavior.
- Bot owns policy/model and action selection.
- First local message is `hello`.
- Runtime loop is agent `welcome`/`turn`/`shutdown` and bot `action`.

## Transport

- UTF-8
- one JSON object per line (JSONL)

Envelope:

```json
{
  "v": "0.2",
  "type": "message_type",
  "id": "optional",
  "payload": {}
}
```

Control request correlation:

- When a control request includes `id`, the agent echoes the same `id` in the response envelope.

## Sockets

- Bot socket: `--listen` (required)
- Control socket: `--control-listen` (optional, defaults to `<listen>.control`)
- Server socket: `--server` (optional)

The control socket is intentionally for client-to-agent orchestration only.
It does not forward raw BBS server commands.

## Bot -> Agent

### `hello` (required first)

```json
{
  "v": "0.2",
  "type": "hello",
  "payload": {
    "name": "my_bot",
    "owner_token": "owner_abc123",
    "capabilities": ["any"],
    "credentials_file": "my_bot_credentials.txt",
    "bot_id": "",
    "bot_secret": ""
  }
}
```

Notes:

- `name` in `hello` is informational only and does **not** override the agent's registered name (which is controlled by the client via `--name` CLI flag). The agent ignores bot-provided names to ensure clients maintain control over bot identity.
- `capabilities` may be array or CSV (`capabilities_csv`).
- `credentials_file` optional; default `<name>_credentials.txt`.
- empty `bot_id`/`bot_secret` requests new identity issuance.

### `action`

```json
{
  "v": "0.2",
  "type": "action",
  "payload": {
    "action": "3"
  }
}
```

### `log` (optional)

```json
{
  "v": "0.2",
  "type": "log",
  "payload": {
    "level": "info",
    "message": "picked action=3"
  }
}
```

## Control Client -> Agent

### `ping`

Health check for control connectivity.

```json
{
  "v": "0.2",
  "type": "ping",
  "payload": {}
}
```

### `status`

Request current control-plane status.

```json
{
  "v": "0.2",
  "type": "status",
  "payload": {}
}
```

### `server_access`

Request the current server access metadata captured during REGISTER.

```json
{
  "v": "0.2",
  "type": "server_access",
  "payload": {}
}
```

### `launch`

Mark agent lifecycle state as attached for client orchestration.

```json
{
  "v": "0.2",
  "type": "launch",
  "id": "req-launch-1",
  "payload": {
    "reason": "client_boot"
  }
}
```

### `detach`

Mark agent lifecycle state as detached for client orchestration.

```json
{
  "v": "0.2",
  "type": "detach",
  "id": "req-detach-1",
  "payload": {
    "reason": "client_shutdown"
  }
}
```

### `lifecycle`

Read current lifecycle orchestration state.

```json
{
  "v": "0.2",
  "type": "lifecycle",
  "id": "req-lifecycle-1",
  "payload": {}
}
```

### `quit`

Ask the agent process to stop.

```json
{
  "v": "0.2",
  "type": "quit",
  "payload": {
    "reason": "client_exit"
  }
}
```

### `server_connect`

Dynamically register with a server (deploy flow). Used when the client wants to change servers or retry registration after initial failure.

The agent will attempt to connect to the provided server endpoint, send a REGISTER command with its locally-configured identity, and capture the returned session metadata.

**Request:**

```json
{
  "v": "0.2",
  "type": "server_connect",
  "id": "req-deploy-1",
  "payload": {
    "server": "127.0.0.1:8080"
  }
}
```

**Successful response:**

```json
{
  "v": "0.2",
  "type": "server_connect",
  "id": "req-deploy-1",
  "payload": {
    "session_id": 12,
    "bot_id": "bot_abcd1234",
    "bot_secret": "secret_xyz789",
    "owner_token": "owner_token_abc123",
    "dashboard_host": "127.0.0.1",
    "dashboard_port": "3000",
    "dashboard_endpoint": "127.0.0.1:3000",
    "arena_id": null,
    "capabilities": "any"
  }
}
```

**Error response (e.g., cannot reach server):**

```json
{
  "v": "0.2",
  "type": "control_error",
  "id": "req-deploy-1",
  "payload": {
    "error": "server_connect_failed",
    "message": "dial tcp: lookup 127.0.0.1: no such host"
  }
}
```

**Notes:**

- The agent uses its local `--name` parameter (set by the client from the bot profile) as the bot identity in the REGISTER command.
- The bot cannot override the name via its `hello` message; the client controls the name designation.
- Multiple candidates may be tried by the client (e.g., normalized loopback IPs) if the first fails.
- Session metadata is cached by the agent for retrieval via `server_access` command.

## Agent -> Bot

### `welcome`

Sent once after successful registration and first arena attach.

```json
{
  "v": "0.2",
  "type": "welcome",
  "payload": {
    "agent_name": "bbs-agent",
    "agent_version": "0.2.0",
    "server": "localhost:8080",
    "session_id": 12,
    "arena_id": 3,
    "player_id": 1,
    "env": "mygame",
    "time_limit_ms": 1000,
    "effective_time_limit_ms": 1200,
    "capabilities": ["any"]
  }
}
```

### `turn`

Sent when actionable state is available.

```json
{
  "v": "0.2",
  "type": "turn",
  "payload": {
    "step": 7,
    "deadline_ms": 1200,
    "obs": {
      "raw_state": "{\"board\":[...],\"turn\":1}",
      "state_obj": {
        "board": [[0,0,0,0,0,0,0]],
        "turn": 1
      },
      "turn_player": 1,
      "your_turn": true,
      "source": "server_data"
    },
    "response": {
      "type": "move",
      "status": "ok",
      "payload": "accepted"
    },
    "reward": 0.0,
    "done": false,
    "truncated": false
  }
}
```

Interpretation notes:

- In two-player games, `your_turn` indicates when to act.
- In autonomous or solo-player modes, each actionable step is still sent through `turn`.
- `done`/`truncated` indicate terminal/truncated rollout conditions.
- Rewards are normalized by current bridge logic (`1.0` win, `-1.0` loss, `0.0` draw/default).

### `shutdown`

```json
{
  "v": "0.2",
  "type": "shutdown",
  "payload": {
    "reason": "agent_exit"
  }
}
```

## Agent -> Control Client

### `control_hello`

Sent immediately when a control client connects.

```json
{
  "v": "0.2",
  "type": "control_hello",
  "payload": {
    "agent_name": "bbs-agent",
    "agent_version": "0.2.0",
    "server": "localhost:8080",
    "server_connected": true,
    "local_bot_connected": true
  }
}
```

### `pong`

Response to `ping`.

```json
{
  "v": "0.2",
  "type": "pong",
  "payload": {
    "ok": true
  }
}
```

### `status`

Response to control `status` request.

```json
{
  "v": "0.2",
  "type": "status",
  "payload": {
    "name": "agent_bot",
    "server": "localhost:8080",
    "server_connected": true,
    "session_id": 12,
    "bot_id": "bot_abcd1234",
    "arena_id": 3,
    "player_id": 1,
    "awaiting_action": false
  }
}
```

### `server_access`

Response to control `server_access` request.

```json
{
  "v": "0.2",
  "type": "server_access",
  "payload": {
    "server": "localhost:8080",
    "server_connected": true,
    "session_id": 12,
    "bot_id": "bot_abcd1234",
    "owner_token": "owner_...",
    "dashboard_host": "localhost",
    "dashboard_port": "3000",
    "dashboard_endpoint": "localhost:3000"
  }
}
```

### `launch_ack`

Response to `launch`.

```json
{
  "v": "0.2",
  "type": "launch_ack",
  "id": "req-launch-1",
  "payload": {
    "attached": true,
    "reason": "client_boot",
    "changed_at": "2026-03-23T12:00:00Z"
  }
}
```

### `detach_ack`

Response to `detach`.

```json
{
  "v": "0.2",
  "type": "detach_ack",
  "id": "req-detach-1",
  "payload": {
    "attached": false,
    "reason": "client_shutdown",
    "changed_at": "2026-03-23T12:05:00Z"
  }
}
```

### `lifecycle`

Response to `lifecycle` request.

```json
{
  "v": "0.2",
  "type": "lifecycle",
  "id": "req-lifecycle-1",
  "payload": {
    "attached": true,
    "reason": "client_boot",
    "changed_at": "2026-03-23T12:00:00Z"
  }
}
```

### `quit_ack`

Acknowledgement sent before shutdown for `quit` requests.

```json
{
  "v": "0.2",
  "type": "quit_ack",
  "payload": {
    "ok": true,
    "reason": "client_exit"
  }
}
```

### `control_error`

Generic error response for control requests.

```json
{
  "v": "0.2",
  "type": "control_error",
  "payload": {
    "error": "forbidden_type",
    "type": "server_command",
    "message": "server command passthrough is intentionally unsupported on control socket"
  }
}
```

Known `error` values:

- `invalid_json`
- `unsupported_version`
- `unsupported_type`
- `invalid_payload`
- `forbidden_type`
