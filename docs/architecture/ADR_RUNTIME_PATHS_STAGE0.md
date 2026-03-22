# ADR: Runtime Path Contract (Stage 0)

## Status

Accepted (initial path contract for packaging-friendly runtime layout).

## Date

2026-03-22

## Context

The server currently depends on working-directory-relative paths in a few critical places, most notably dashboard template loading and default runtime file locations.

This creates friction for:

- Running from different current working directories
- Containerized deployments
- Future native packaging (`.deb`, systemd)
- Cross-platform installers (Linux/macOS/Windows)

We need a single runtime path model that can be reused across packaging targets.

## Decision

Introduce a central runtime path resolver in `cmd/bbs-server` and treat resolved paths as canonical startup configuration.

### Environment Variables

Primary path contract variables:

- `BBS_SERVER_HOME`
- `BBS_CONFIG_DIR`
- `BBS_DATA_DIR`
- `BBS_TEMPLATE_DIR`
- `BBS_GAME_PLUGIN_DIR`
- `BBS_SQLITE_PATH`

### Precedence Rules

For each path:

1. Explicit env var override
2. Resolver default

Resolver defaults are anchored to detected runtime context and normalized to absolute paths.

### Initial Defaults

- `BBS_SERVER_HOME`: current working directory
- `BBS_CONFIG_DIR`: `${BBS_SERVER_HOME}/config`
- `BBS_DATA_DIR`: `${BBS_SERVER_HOME}/data`
- `BBS_SQLITE_PATH`: `${BBS_DATA_DIR}/bbs.sqlite3`
- `BBS_TEMPLATE_DIR`: first existing of:
  - `${cwd}/templates`
  - `${cwd}/cmd/bbs-server/templates`
  - `${BBS_SERVER_HOME}/templates`
  - `${BBS_SERVER_HOME}/cmd/bbs-server/templates`
- `BBS_GAME_PLUGIN_DIR`: first existing of:
  - `${cwd}/plugins/games`
  - `${cwd}/cmd/bbs-server/plugins/games`
  - `${BBS_SERVER_HOME}/plugins/games`
  - `${BBS_SERVER_HOME}/cmd/bbs-server/plugins/games`

If no plugin/template candidate exists, the resolver uses the corresponding `${BBS_SERVER_HOME}`-based default path.

### Startup Wiring

- Bootstrap resolves runtime paths once and caches them.
- SQLite parent directory is created before opening the database.
- Dashboard template parsing uses `BBS_TEMPLATE_DIR` resolved path.
- If `BBS_GAME_PLUGIN_DIR` is not set, bootstrap sets it from resolved path for plugin discovery consistency.
- Startup logs print effective runtime paths.

## Consequences

Positive:

- Greatly reduces dependence on process working directory.
- Makes local/dev, Docker, and native package layouts converge on one contract.
- Adds explicit override surface required for installers and service managers.

Trade-offs:

- Introduces additional startup configuration surface.
- Linux FHS defaults (`/etc`, `/var/lib`, `/var/log`) are not yet enforced by default in this stage.

## Follow-up

- ✅ **Stage 0a** (complete): Add Linux FHS packaging profile via `BBS_PACKAGING_MODE=linux-fhs` for enterprise/`.deb` deployments.
  - See [docs/deployment/LINUX_PACKAGING_PROFILE.md](../deployment/LINUX_PACKAGING_PROFILE.md) for `.deb` and systemd examples.
- Add platform-specific default mappings for macOS and Windows installers.
- Further hardening: Production mode that enforces FHS or explicit overrides (prevents dev paths in production).
