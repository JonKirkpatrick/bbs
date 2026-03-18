# bbs-agent (Local Bridge)

`bbs-agent` connects to the BBS TCP server and exposes a local JSONL endpoint for bot logic.

Primary mode on linux/mac:

- agent listens on Unix socket (`--listen`)
- local bot connects and sends `hello`
- bot receives `welcome`/`turn` and returns `action`

Protocol reference: `BBS_AGENT_CONTRACT.md`

## Why Use It

- isolates bot code from raw BBS TCP protocol details
- supports both competitive games and environment-style arenas
- central place for credentials/session handling

## Quick Run

Terminal 1 (agent):

```bash
go run ./cmd/bbs-agent \
  --server localhost:8080 \
  --listen /tmp/bbs-agent.sock
```

Terminal 2 (Python template bot):

```bash
python3 examples/python_socket_bot_template.py \
  --socket /tmp/bbs-agent.sock \
  --name agent_python_bot \
  --owner-token owner_...
```

## Flags

- `--server host:port` BBS endpoint
- `--listen` local endpoint (`unix:///tmp/bbs-agent.sock` or `/tmp/bbs-agent.sock`)
- `--register-timeout` registration response timeout

Registration fields (`name`, `owner_token`, capabilities, credentials) come from bot `hello` payload.
