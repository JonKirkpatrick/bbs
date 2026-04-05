# Dashboard Remote Quickstart

This guide is for connecting a bot to an existing Build-a-Bot Stadium server.

It assumes:

- you can open the dashboard URL in a browser
- the server is running elsewhere (not your local machine)

## What You Need

- dashboard URL (for example `https://bbs.example.com`)
- bot TCP endpoint (for example `bbs.example.com:8080`)
- Python 3.9+ for included templates (optional but convenient)

## Recommended Path: Dashboard + `bbs-agent`

1. Open dashboard in browser.
2. Click `Register Bot`.
3. Copy owner token from the server access metadata card.
4. Note Bot Host/IP and Bot Port from same panel.
5. Start local bridge agent:

```bash
go run ./cmd/bbs-agent \
  --server bbs.example.com:8080 \
  --listen /tmp/bbs-agent.sock
```

6. Start local bot connected to bridge:

```bash
python3 examples/python_socket_bot_template.py \
  --socket /tmp/bbs-agent.sock \
  --name my_bot \
  --owner-token owner_abc123...
```

7. Return to dashboard and wait for linked session status.
8. Use owner controls to create/join/leave/disconnect arenas.

## Arena Creation UX

Create Arena in both owner and admin panels now uses the live runtime game catalog.

- Game is selected from dropdown (not free-text)
- Per-game args are rendered dynamically
- Time/handicap controls auto-adjust for games that do not use move clocks

So if server operators enable plugins and install valid manifests, those games can appear in your dropdown without dashboard code changes.

## Alternative: Direct TCP Bot

If you prefer to handle protocol directly:

```bash
python3 examples/python_bot_template.py \
  --server bbs.example.com:8080 \
  --name my_bot \
  --owner-token owner_abc123...
```

Returning identity run:

```bash
python3 examples/python_bot_template.py \
  --server bbs.example.com:8080 \
  --name my_bot \
  --credentials-file my_bot_credentials.txt \
  --owner-token owner_abc123...
```

## Useful Commands (after register)

```text
LIST
CREATE mygame 1000 false board_size=8
JOIN 1 0
MOVE 3
LEAVE
QUIT
```

## Troubleshooting

- `Must REGISTER first`: registration not completed before command.
- `owner token is invalid`: refresh token from the server access metadata card and retry.
- `owner token is already linked`: disconnect old linked bot first.
- `arena full`: chosen arena already has required players.

## Security Notes

- `owner_token`, `bot_id`, and `bot_secret` are sensitive.
- Treat credentials files like secrets.
- Current bot channel is plain TCP; prefer trusted networks or external tunnel/TLS.

If you are using the desktop client, the active bot session card will populate JOIN targets from the selected server's arena list once the server detail view has loaded.
