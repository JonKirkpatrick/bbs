# BBS Agent Contract v0.2 (Local Bridge)

This contract defines the local JSONL protocol between:

- `bbs-agent` (bridge to BBS TCP server)
- a local bot process (usually over Unix socket on linux/mac)

`bbs-agent` does not launch bot subprocesses. Bot authors connect to the endpoint exposed by `--listen`.

## Purpose

The bridge keeps server/network details inside `bbs-agent` so bot code can focus on decisions.

This applies to both:

- competitive games
- single-agent environment-style arenas

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

## Bot -> Agent

### `hello` (required first)

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

- `name` defaults to `agent_bot` when omitted.
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
    "env": "connect4",
    "time_limit_ms": 1000,
    "effective_time_limit_ms": 1200,
    "capabilities": ["connect4"]
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
- In single-agent environments, each actionable step is still sent through `turn`.
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
