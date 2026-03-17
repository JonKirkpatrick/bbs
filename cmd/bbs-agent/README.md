# bbs-agent (Local Bridge)

`bbs-agent` connects to the BBS TCP server and exposes a local bot endpoint.

Primary mode (linux/mac):

- agent listens on a Unix socket (`--listen`)
- bot connects locally and sends a `hello` message
- bot and agent exchange `welcome`/`turn`/`action` messages over JSONL

Protocol details: `BBS_AGENT_CONTRACT.md`

## Quick Run

Terminal 1 (agent):

```bash
go run ./cmd/bbs-agent \
  --server localhost:8080 \
  --listen /tmp/bbs-agent.sock
```

Terminal 2 (python template bot):

```bash
python3 examples/python_socket_bot_template.py \
  --socket /tmp/bbs-agent.sock \
  --name agent_python_bot \
  --owner-token owner_...
```

## Flags

- `--server host:port` BBS server endpoint
- `--listen` local Unix socket endpoint (`unix:///tmp/bbs-agent.sock` or `/tmp/bbs-agent.sock`)
- `--register-timeout` registration response timeout

Bot registration fields (`name`, `owner_token`, `capabilities`, credentials) are supplied by the bot in the initial `hello` payload.

