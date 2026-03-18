# TrueNAS SCALE Deployment (No Docker)

This setup runs `bbs-server` directly as background process on TrueNAS host.

## What You Get

- always-on `bbs-server`
- dashboard/viewer on `:3000`
- bot endpoint on `:8080`
- optional nightly update/rebuild/restart

## Requirements

- host shell access with `git`
- optionally `go` for source builds

State note: runtime state is in-memory; restart clears sessions/arenas/history.

## 1. Clone To Persistent Dataset

```bash
mkdir -p /mnt/<pool>/apps
cd /mnt/<pool>/apps
git clone https://github.com/JonKirkpatrick/bbs.git
cd bbs/deploy/truenas-no-docker
```

## 2. Configure Environment

```bash
cp .env.example .env
```

Set:

```bash
BBS_DASHBOARD_ADMIN_KEY=<long-random-secret>
```

Optional:

- `TZ=UTC`
- `BBS_BINARY_URL`
- `BBS_RELEASE_OWNER`
- `BBS_RELEASE_REPO`
- `BBS_RELEASE_TAG`
- `BBS_RELEASE_ASSET`

## 3. Make Scripts Executable

```bash
chmod +x scripts/*.sh
```

## 4a. Build From Source (Go Installed)

```bash
./scripts/build-server.sh
./scripts/start-server.sh
./scripts/status-server.sh
```

## 4b. Download Release Binary (No Go)

```bash
./scripts/update-from-release.sh
./scripts/status-server.sh
```

Expected release assets:

- `bbs-server-linux-amd64`
- `bbs-server-linux-arm64`

## Optional: Enable Game Plugins

Process plugins can be enabled in `.env` used by start scripts:

```bash
BBS_ENABLE_GAME_PLUGINS=true
BBS_GAME_PLUGIN_DIR=/mnt/<pool>/apps/bbs/plugins/games
```

Place plugin binaries and `*.json` manifests in that directory.

## Runtime Endpoints

- dashboard: `http://<truenas-ip>:3000`
- bot server: `<truenas-ip>:8080`

## Daily Operations

```bash
./scripts/status-server.sh
./scripts/restart-server.sh
./scripts/stop-server.sh
```

Logs:

- runtime: `scripts/bbs-server.log`
- build: `scripts/build.log`
- source update: `scripts/update.log`
- release update: `scripts/update-release.log`

## Optional Cron Jobs

Source build flow:

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas-no-docker/scripts/update-if-changed.sh
```

Release binary flow:

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas-no-docker/scripts/update-from-release.sh
```

Optional forced restart:

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas-no-docker/scripts/restart-server.sh
```

## Startup On Boot

Add cron `@reboot` task:

```bash
/usr/bin/env bash /mnt/<pool>/apps/bbs/deploy/truenas-no-docker/scripts/start-server.sh
```

## Troubleshooting

- `go: command not found`: use release updater path.
- `missing .env`: run `cp .env.example .env`.
- immediate exit: check `scripts/bbs-server.log`.
