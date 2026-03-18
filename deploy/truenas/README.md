# TrueNAS SCALE Deployment (Always-On `bbs-server`)

This guide deploys `bbs-server` with Docker Compose on TrueNAS SCALE.

## What This Deploys

- bot endpoint: `:8080`
- dashboard/viewer endpoint: `:3000`
- restart policy: `unless-stopped`

## Prerequisites

- TrueNAS SCALE shell access
- Docker engine + compose plugin
- persistent dataset path for repo clone

## 1. Clone To Persistent Dataset

```bash
mkdir -p /mnt/<pool>/apps
cd /mnt/<pool>/apps
git clone https://github.com/JonKirkpatrick/bbs.git
cd bbs/deploy/truenas
```

## 2. Create Runtime Env File

```bash
cp .env.example .env
```

Set at least:

```bash
BBS_DASHBOARD_ADMIN_KEY=<long-random-secret>
```

Optional port/tz vars:

- `BBS_BOT_PORT`
- `BBS_DASHBOARD_PORT`
- `TZ`

## 3. Build And Start

```bash
docker compose --env-file .env up -d --build
```

## 4. Verify

```bash
docker compose ps
docker compose logs --tail=200 bbs-server
```

Dashboard:

- `http://<truenas-ip>:3000`

Bot endpoint:

- `<truenas-ip>:8080`

## Optional: Enable Game Plugins

The server can load process plugins from manifest directory (`plugins/games` by default).

To enable in containerized deployment:

1. expose env vars in compose runtime:
   - `BBS_ENABLE_GAME_PLUGINS=true`
   - `BBS_GAME_PLUGIN_DIR=/app/plugins/games`
2. mount plugin directory into container (binary + `*.json` manifests).

Example manifest fields:

- `protocol_version`
- `name`
- `display_name`
- `executable`
- `supports_move_clock`
- `supports_handicap`
- `args`

## Optional Nightly Automation

Scripts:

- `scripts/update-if-changed.sh`
- `scripts/restart-server.sh`

Suggested cron jobs:

1. nightly update check

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas/scripts/update-if-changed.sh
```

2. optional nightly restart

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas/scripts/restart-server.sh
```

## Notes

- runtime state is in-memory (restart clears sessions/arenas/history)
- keep host clone clean for fast-forward update scripts
- script logs:
  - `deploy/truenas/scripts/update.log`
  - `deploy/truenas/scripts/restart.log`
