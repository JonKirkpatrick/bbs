# BBS Agent Contract v0.2 (Local Bridge)

This contract defines the local JSONL protocol between:

- `bbs-agent` (network bridge to BBS server)
- local bot process (connected over Unix socket on linux/mac)

`bbs-agent` no longer launches bot subprocesses. Bot authors connect to the
local endpoint exposed by `--listen`.

## Design

- Agent owns BBS TCP networking and registration.
- Bot owns decision logic only.
- First local bot message is a handshake (`hello`).
- Runtime loop is `welcome`/`turn`/`shutdown` from agent and `action` from bot.

## Transport

- Encoding: UTF-8
- Framing: one JSON object per line

Envelope:

```json
{
  "v": "0.2",
  "type": "message_type",
  "id": "optional",
  "payload": {}
}
```

## Bot -> Agent

### `hello` (required first message)

```json
{
  "v": "0.2",
  "type": "hello",
  "payload": {
    "name": "my_bot",
    "owner_token": "owner_abc123",
    "capabilities": ["connect4"],
    "credentials_file": "my_bot_credentials.txt",
    "bot_id": "",
    "bot_secret": ""
  }
}
```

Notes:

- `name` defaults to `agent_bot` if omitted.
- `capabilities` may be array or CSV (`capabilities_csv`).
- `credentials_file` is optional; default is `<name>_credentials.txt`.
- Empty `bot_id`/`bot_secret` requests new identity issuance during register.

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

## Agent -> Bot

### `welcome`

Sent once after successful `JOIN`.

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
    "env": "connect4",
    "time_limit_ms": 1000,
    "effective_time_limit_ms": 1200,
    "capabilities": ["connect4"]
  }
}
```

### `turn`

Sent when an actionable state is available.

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
        "board": [[0,0,0,0,0,0,0], [0,0,0,0,0,0,0]],
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

Notes:

- `obs` is game/environment state.
- `response` carries result of a prior action when available.
- `done` and `truncated` indicate terminal conditions.
- Terminal rewards: `1.0` win, `-1.0` loss, `0.0` draw.

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
