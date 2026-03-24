# Linux Packaging Profile (FHS-Compliant Deployment)

## Overview

The Build-a-Bot Stadium server supports a Linux Filesystem Hierarchy Standard (FHS) packaging profile that auto-configures paths for native package installs (`.deb`, systemd, enterprise deployments).

When enabled, this profile uses standard Linux directories instead of development-friendly defaults.

## Activation

Enable the Linux FHS profile by setting:

```bash
export BBS_PACKAGING_MODE=linux-fhs
```

Then run the server. All paths automatically map to FHS locations.

## Path Mapping

### Development Mode (default)

No environment variable needed:

```bash
go run ./cmd/bbs-server
```

Paths:

- Config: `${cwd}/config`
- Data: `${cwd}/data`
- SQLite: `${cwd}/data/bbs.sqlite3`
- Templates: `${cwd}/templates` or `${cwd}/cmd/bbs-server/templates`
- Plugins: `${cwd}/plugins/games` or `${cwd}/cmd/bbs-server/plugins/games`

### Linux FHS Mode

```bash
BBS_PACKAGING_MODE=linux-fhs /opt/bbs/bin/bbs-server
```

Standard FHS locations:

| Directory | Default FHS Path | Purpose |
|-----------|------------------|---------|
| `BBS_SERVER_HOME` | `/opt/bbs` | Application base |
| `BBS_CONFIG_DIR` | `/etc/bbs` | Configuration files |
| `BBS_DATA_DIR` | `/var/lib/bbs` | Persistent state (SQLite, plugins) |
| `BBS_SQLITE_PATH` | `/var/lib/bbs/bbs.sqlite3` | SQLite database |
| `BBS_TEMPLATE_DIR` | `/opt/bbs/templates` or `/usr/lib/bbs/templates` | Dashboard templates |
| `BBS_GAME_PLUGIN_DIR` | `/var/lib/bbs/plugins/games` or `/usr/lib/bbs/plugins/games` | Installed game plugins |

## `.deb` Installation Example

### Package Contents

```
/opt/bbs/
  bin/bbs-server          # Binary
  templates/              # Dashboard template files
/etc/bbs/
  bbs.env                 # Environment config
/var/lib/bbs/
  plugins/games/          # Plugin directory
  bbs.sqlite3             # Database (created on first run)
/usr/lib/systemd/system/
  bbs-server.service      # systemd service unit
```

### systemd Service File

Example `/usr/lib/systemd/system/bbs-server.service`:

```ini
[Unit]
Description=Build-a-Bot Stadium Server
After=network.target

[Service]
Type=simple
User=bbs
Group=bbs
WorkingDirectory=/var/lib/bbs
EnvironmentFile=/etc/bbs/bbs.env
Environment="BBS_PACKAGING_MODE=linux-fhs"
ExecStart=/opt/bbs/bin/bbs-server --dash 3000 --stadium 8080
Restart=always
RestartSec=10s

StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

### Environment File

`/etc/bbs/bbs.env`:

```bash
# Optional overrides (if not set, FHS defaults apply)
# BBS_SERVER_HOME=/opt/bbs
# BBS_CONFIG_DIR=/etc/bbs
# BBS_DATA_DIR=/var/lib/bbs
# BBS_SQLITE_PATH=/var/lib/bbs/bbs.sqlite3

# Administrative features
BBS_DASHBOARD_ADMIN_KEY=required-admin-secret

# Federation (optional)
# BBS_ENABLE_MOCK_GLOBAL_REGISTRY=true
# BBS_FEDERATION_OUTBOX_URL=https://central-registry.example.com/inbound
# BBS_FEDERATION_OUTBOX_TOKEN=server-api-token
```

### User/Permission Setup

```bash
# Create bbs user and group
sudo useradd -r -s /bin/false -d /var/lib/bbs -m bbs

# Ensure directory ownership
sudo chown -R bbs:bbs /var/lib/bbs
sudo chown -R root:root /opt/bbs
sudo chown root:root /etc/bbs/bbs.env && sudo chmod 600 /etc/bbs/bbs.env

# Set permissions
sudo chmod 0755 /var/lib/bbs
sudo chmod 0755 /etc/bbs
```

### Service Control

```bash
# Enable on boot
sudo systemctl enable bbs-server

# Start service
sudo systemctl start bbs-server

# View logs
sudo journalctl -u bbs-server -f

# Stop service
sudo systemctl stop bbs-server
```

## Docker Deployment

For Docker, you can still use FHS paths or custom volume mounts:

```bash
docker run -it \
  -e BBS_PACKAGING_MODE=linux-fhs \
  -e BBS_DATA_DIR=/data \
  -v bbs-data:/data \
  -p 8080:8080 \
  -p 3000:3000 \
  bbs-server
```

Or bind-mount for host directories:

```bash
docker run -it \
  -e BBS_PACKAGING_MODE=linux-fhs \
  -v /etc/bbs:/etc/bbs:ro \
  -v /var/lib/bbs:/var/lib/bbs \
  -v /opt/bbs/templates:/opt/bbs/templates:ro \
  -p 8080:8080 \
  -p 3000:3000 \
  bbs-server
```

## Environment Override Precedence

Explicit env vars always take precedence:

1. `--stadium` / `--dash` command-line flags (ports)
2. `BBS_PACKAGING_MODE=linux-fhs` activates FHS defaults
3. Explicit `BBS_*_DIR` / `BBS_SQLITE_PATH` overrides FHS defaults
4. FHS defaults (if packaging mode enabled) or dev defaults (if not)

Example: Linux FHS mode with custom plugins directory

```bash
BBS_PACKAGING_MODE=linux-fhs \
BBS_GAME_PLUGIN_DIR=/mnt/nfs-plugins/games \
/opt/bbs/bin/bbs-server
```

This uses FHS for config, data, templates, but custom location for plugins.

## Build and Install

### Create Binary for Packaging

```bash
# From repo root
make build-server

# Binary: /tmp/bbs-build/bbs-server
```

### Example Postinst Script

For dpkg postinstall (from `.deb`), ensure directories exist:

```bash
#!/bin/bash
set -e

# Ensure data directories exist
mkdir -p /var/lib/bbs/plugins/games
mkdir -p /etc/bbs

# Set permissions
chown bbs:bbs /var/lib/bbs
chmod 0755 /var/lib/bbs

# Set config permissions
chmod 0750 /etc/bbs

echo "BBS installation complete. Start with: systemctl start bbs-server"
```

## Migration Path: Development → Packaged

1. Develop with default mode:
   ```bash
   cd cmd/bbs-server && go run .
   ```

2. Test FHS mode locally:
   ```bash
   BBS_PACKAGING_MODE=linux-fhs go run ./cmd/bbs-server
   ```

3. Package for distribution using same codebase (no changes needed).

4. Deploy with systemd:
   ```bash
   sudo systemctl start bbs-server
   ```

## Logs and Diagnostics

### Startup Paths

When the server starts, it logs effective paths:

```
Runtime paths: home=/opt/bbs data=/var/lib/bbs sqlite=/var/lib/bbs/bbs.sqlite3 templates=/opt/bbs/templates plugins=/var/lib/bbs/plugins/games
```

### Check Packaging Mode

```bash
# Enable debug admin endpoints with key
BBS_PACKAGING_MODE=linux-fhs \
BBS_DASHBOARD_ADMIN_KEY=debug-key \
go run ./cmd/bbs-server

# Query admin debug endpoints
curl http://localhost:3000/admin/debug/server-identity?admin_key=debug-key
```

## Troubleshooting

### Paths not found in FHS mode

Check env var spelling and enable debug logs:

```bash
BBS_PACKAGING_MODE=linux-fhs go run ./cmd/bbs-server 2>&1 | grep "Runtime paths"
```

### Permission denied on data directory

Ensure `bbs` user owns `/var/lib/bbs`:

```bash
sudo chown -R bbs:bbs /var/lib/bbs
```

### Plugin discovery issues

Verify plugin directory in FHS mode:

```bash
ls -la /var/lib/bbs/plugins/games/
```

Set explicit plugin directory if needed:

```bash
BBS_PACKAGING_MODE=linux-fhs \
BBS_GAME_PLUGIN_DIR=/custom/path/plugins \
/opt/bbs/bin/bbs-server
```
