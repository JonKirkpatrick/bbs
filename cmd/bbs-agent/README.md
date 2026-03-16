# bbs-agent (MVP)

`bbs-agent` is a local bridge process that:

- launches a worker process
- connects to the BBS TCP server
- performs `REGISTER`
- forwards server events/state to the worker
- forwards worker `move`/`command` messages back to BBS

It implements the draft contract in `BBS_AGENT_CONTRACT.md`.

## Quick Run (Python Worker Template)

From repository root:

```bash
go run ./cmd/bbs-agent \
  --server localhost:8080 \
  --name agent_python_bot \
  --owner-token owner_... \
  --worker python3 \
  --worker-arg examples/python_worker_contract_template.py
```

Optional:

- `--credentials-file path/to/creds.txt`
- `--capabilities connect4`
- `--worker-arg ...` (repeatable)

## Notes

- The agent writes credentials to `<name>_credentials.txt` if the server issues a new identity during register.
- The worker template derives Connect4 legal columns from `state.state_obj.board` and only emits a move when `your_turn`/`turn_player` indicates it should act.
- Current BBS transport is plain TCP; treat credentials and owner tokens as sensitive.
