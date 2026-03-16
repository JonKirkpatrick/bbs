# BBS Agent Contract (Draft v0.1)

This document defines a language-agnostic contract between:

- `bbs-agent`: a local sidecar process that speaks TCP + BBS protocol to the server
- `worker`: your bot logic process (Python, JS, Java, Rust, etc.)

Goal: bot authors should mostly implement decision logic, not networking and wire parsing.

## Status

This is a draft contract intended to guide a first implementation.

## Design Goals

- One stable worker interface across languages
- JSON lines over `stdin`/`stdout` so wrappers are easy in any language
- Minimal required worker behavior for a usable bot
- Explicit versioning for compatibility

## Non-Goals (for v0.1)

- Full game-specific schema standardization
- Strong cryptographic channel/auth upgrades
- Hot-reload plugin system

## Process Topology

```text
+-------------------+       JSONL stdin/stdout       +-------------------+
| Worker (any lang) | <-----------------------------> | bbs-agent (Go)    |
+-------------------+                                 +-------------------+
                                                             |
                                                             | TCP + BBS protocol
                                                             v
                                                     +-------------------+
                                                     | BBS Server        |
                                                     +-------------------+
```

## Worker Transport

- Encoding: UTF-8
- Framing: one JSON object per line (`\n` terminated)
- Direction:
  - agent -> worker: events and state
  - worker -> agent: decisions and control intents

## Envelope

All messages use this envelope:

```json
{
  "v": "0.1",
  "type": "message_type",
  "id": "optional-correlation-id",
  "payload": {}
}
```

Fields:

- `v`: contract version string (required)
- `type`: message type (required)
- `id`: optional correlation ID for request/response pairing
- `payload`: JSON object (required, can be empty)

## Agent -> Worker Messages

### `hello`
Sent once at worker startup.

```json
{
  "v": "0.1",
  "type": "hello",
  "payload": {
    "agent_name": "bbs-agent",
    "agent_version": "0.1.0",
    "server": "host:port"
  }
}
```

### `registered`
Sent after successful BBS `REGISTER`.

```json
{
  "v": "0.1",
  "type": "registered",
  "payload": {
    "session_id": 12,
    "bot_id": "bot_...",
    "is_new_identity": false,
    "auth_mode": "id+secret+owner_token"
  }
}
```

### `manifest`
Sent when the bot joins an arena and server returns join metadata.

```json
{
  "v": "0.1",
  "type": "manifest",
  "payload": {
    "arena_id": 3,
    "player_id": 1,
    "game": "connect4",
    "time_limit_ms": 1000,
    "effective_time_limit_ms": 1200,
    "handicap_enabled": true,
    "handicap_percent": 20
  }
}
```

### `state`
Sent when BBS emits game-state data (`type=data`) or equivalent view updates.

```json
{
  "v": "0.1",
  "type": "state",
  "payload": {
    "raw_state": "{\"board\":[...],\"turn\":2}",
    "state_obj": {
      "board": [[0,0,0,0,0,0,0], [0,0,0,0,0,0,0]],
      "turn": 2
    },
    "turn_player": 2,
    "your_turn": false,
    "source": "server_data"
  }
}
```

Notes:

- `raw_state` is always forwarded as the canonical state string when available.
- `state_obj` is optional and only present when the agent can parse `raw_state` as JSON object.
- `turn_player` and `your_turn` are optional convenience fields inferred by the agent.
- `legal_moves` is intentionally not required in v0.1; workers can compute legality from game state.

### `event`
Generic game/runtime event for non-state updates.

```json
{
  "v": "0.1",
  "type": "event",
  "payload": {
    "name": "gameover",
    "data": {
      "match_id": 22,
      "is_draw": false,
      "winner_player_id": 1
    }
  }
}
```

### `error`
Agent-level or server-level error.

```json
{
  "v": "0.1",
  "type": "error",
  "payload": {
    "code": "register_failed",
    "message": "owner token is invalid",
    "retryable": false
  }
}
```

### `shutdown`
Agent is terminating worker session.

```json
{
  "v": "0.1",
  "type": "shutdown",
  "payload": {
    "reason": "operator_exit"
  }
}
```

## Worker -> Agent Messages

### `hello_ack`
Required response to `hello`.

```json
{
  "v": "0.1",
  "type": "hello_ack",
  "payload": {
    "worker_name": "my_worker",
    "worker_version": "0.1.0",
    "language": "python"
  }
}
```

### `move`
Worker asks agent to send `MOVE <value>`.

```json
{
  "v": "0.1",
  "type": "move",
  "payload": {
    "move": "3"
  }
}
```

### `command`
Optional escape hatch for advanced usage (`LIST`, `WATCH`, `LEAVE`, etc.).

```json
{
  "v": "0.1",
  "type": "command",
  "payload": {
    "text": "LIST"
  }
}
```

### `set_profile`
Optional profile update requests before/after registration.

```json
{
  "v": "0.1",
  "type": "set_profile",
  "payload": {
    "name": "new_display_name",
    "capabilities": ["connect4"]
  }
}
```

### `log`
Worker log output forwarded by agent.

```json
{
  "v": "0.1",
  "type": "log",
  "payload": {
    "level": "info",
    "message": "thinking..."
  }
}
```

## Minimum Worker Behavior

A minimum compliant worker should:

1. Read JSONL from `stdin`
2. Send `hello_ack` after `hello`
3. Handle `manifest` and `state`
4. Optionally emit `move` when it decides a move
5. Exit cleanly when it receives `shutdown`

For Connect4 workers, a common baseline is deriving legal columns from `state_obj.board` instead of relying on server-provided legal move lists.

## Agent Lifecycle (Reference)

1. Agent starts and launches worker process
2. Agent sends `hello`
3. Worker replies `hello_ack`
4. Agent connects to BBS server and performs REGISTER
5. Agent forwards `registered`
6. During gameplay:
   - server `join` -> agent sends `manifest`
   - server `data` -> agent sends `state`
   - worker sends `move` -> agent sends `MOVE ...` to server
   - server events -> agent sends `event`
7. On exit/error, agent sends `shutdown`

## Reliability Semantics

- Delivery is at-most-once per message line
- Worker should tolerate duplicate or late `event` messages
- Worker should treat unknown `type` as ignorable

## Versioning Rules

- Major contract changes bump major version (`0.x` -> `1.x`)
- Agent should reject unsupported major versions with an `error`
- Unknown fields are allowed and should be ignored by both sides

## Security Considerations

- Agent may hold `bot_id`, `bot_secret`, `owner_token` on disk or memory
- Worker should never log secrets in plaintext
- Current BBS transport is plain TCP; use trusted networks or tunnel as needed

## Suggested First Deliverables

1. `cmd/bbs-agent` in Go implementing this JSONL contract
2. Python worker template (see `examples/python_worker_contract_template.py`)
3. One integration test that mocks worker stdin/stdout and validates `move` routing
