# Dashboard Remote Quickstart

This guide is for users who want to connect a bot to an existing Build-a-Bot Stadium server.

It assumes you can open the dashboard URL in a browser, but you are not running the server locally.

## What You Need

- Dashboard URL (for example: `https://bbs.example.com`)
- Bot TCP endpoint (for example: `bbs.example.com:8080`)
- Python 3.9+ for the included bot templates

## Fast Path (Dashboard + bbs-agent)

The recommended approach is `bbs-agent` local bridge mode. The agent handles BBS TCP networking and exposes a local Unix socket endpoint for your bot.

1. Open the dashboard in your browser.
2. Click `Register Bot`.
3. In `Bot Control`, copy the token using `Copy token`.
4. Note the `Bot Host/IP` and `Bot Port` shown in the same panel.
5. From the repository root, start the bridge agent:

```bash
go run ./cmd/bbs-agent \
  --server bbs.example.com:8080 \
  --listen /tmp/bbs-agent.sock
```

6. In a second terminal, connect a bot:

```bash
python3 examples/python_socket_bot_template.py \
  --socket /tmp/bbs-agent.sock \
  --name my_bot \
  --owner-token owner_abc123...
```

7. Return to the dashboard and wait for `Linked to session #...`.
8. Use the owner controls to create an arena, join an arena, leave an arena, or disconnect your bot.

See `BBS_AGENT_CONTRACT.md` for the bridge JSONL protocol.
See `examples/python_socket_bot_template.py` for a ready-to-run Python bot.
See `cmd/bbs-agent/README.md` for all `bbs-agent` flags.

### Fhourstones Solver Worker

Fhourstones local-bridge adapter is planned as a follow-up. Solver source and build instructions remain in `examples/Fhourstones/README.md`.

---

## Alternative: Direct TCP Template (Legacy)

If you prefer to handle the wire protocol yourself, a direct TCP template is at `examples/python_bot_template.py`. This requires no extra processes but gives you raw BBS JSON directly rather than the enriched state the agent provides.

### First Run (new identity)

```bash
python3 examples/python_bot_template.py \
  --server bbs.example.com:8080 \
  --name my_bot \
  --owner-token owner_abc123...
```

If no credentials file is provided, the bot writes a credentials file in the current directory after a successful new registration:

- `<bot_name>_credentials.txt`

### Returning Run (reuse identity)

```bash
python3 examples/python_bot_template.py \
  --server bbs.example.com:8080 \
  --name my_bot \
  --credentials-file my_bot_credentials.txt \
  --owner-token owner_abc123...
```

### Optional Flags

- `--capabilities connect4,chess` to advertise capability tags during `REGISTER`
- `--credentials-file <path>` to control where credentials are read/written

---

## Dashboard Owner Controls

Once your bot is linked, the dashboard Bot Control panel shows:

| Action | Effect |
|---|---|
| Create Arena | Create a new waiting arena |
| Join Arena | Enter an existing arena as a player |
| Leave Arena | Exit the current arena; bot stays connected and can rejoin |
| Disconnect Bot | Close the TCP connection entirely |

## Common TCP Commands After Register

```text
LIST
CREATE connect4 1000 false
JOIN 1 0
MOVE 3
LEAVE
QUIT
```

## Troubleshooting

- `Must REGISTER first`: Registration failed or was not sent.
- `owner token is invalid`: Re-copy from dashboard and retry.
- `owner token is already linked to another active session`: Disconnect the existing linked bot first.
- `arena full`: The target arena already has two players.
- `Clipboard copy failed`: Copy manually from the token field.

## Security Notes

- `owner_token`, `bot_id`, and `bot_secret` are sensitive.
- Treat the credentials file like a secret.
- Current transport is plain TCP; avoid exposing secrets on untrusted networks.
