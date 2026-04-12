# Changelog

All notable changes to Build-a-Bot Stadium are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v0.5.0] - 2026-04-08

### Added
- **Client**: Local bot orchestration with deploy/launch/detach lifecycle, Unix socket IPC, and runtime session tracking
- **Client**: Bot name sanitization (spaces -> underscores) when passing profile identity to agent runtime
- **Client**: Dynamic server registration via `server_connect` without requiring agent restart
- **Client**: Endpoint resilience during deploy through normalized fallback candidate probing
- **Client**: SQLite-backed bot profile persistence with per-server runtime state and credential metadata
- **Client**: Agent control socket support for `server_connect`, `server_access`, and shutdown control paths (JSON v0.2)
- **Docs**: Docusaurus documentation site wired to repository docs content

### Changed
- **Client**: Agent no longer accepts bot name overrides from bot `hello` payloads
- **Client**: Deploy validation now includes endpoint candidate generation and fallback retry behavior
- **Client**: Bot launch arguments constrained to `--socket`; profile name is passed through `--name`
- **Client**: Active bot session cards refresh arena options from the selected server's live arena list
- **Client**: Server-context access metadata resolves from selected known server profile first
- **Server**: Discovery probe behavior updated
- **Server**: Owner token semantics moved from per-session to per-client
- **Server**: Persistent server-side bot registry removed; bot ownership responsibility shifted toward client-side identity/session mapping

### Fixed
- **Client**: Deploy failures when server host metadata used malformed loopback values (for example `127.0.01`)
- **Client**: Agent startup failures for bot profile names containing spaces
- **Client**: Persona-backed local state partitioning for multiple independent SQLite files
- **Client**: Splash animation integration and menu bar polish
- **Client**: Arena viewer integration improvements to reduce dashboard dependency for viewing workflows


## [v0.3.0] - 2026-03-22

### Added
- **Server**: SQLite persistence stage 0 for server identity, match history, bot profiles, and federation outbox
- **Server**: Runtime path contract with environment-overridable locations for templates, plugins, config, and data
- **Server**: Federation infrastructure including outbox worker, retry/backoff publisher, mock registrar, and dedupe receipts
- **Server**: Admin debug endpoints for identity, outbox, and recent matches visibility
- **Server**: Optional token-gated mock federation ingest endpoint (`/federation/mock/ingest`)
- **Infrastructure**: Linux FHS packaging profile with native .deb + systemd support (`BBS_PACKAGING_MODE=linux-fhs`)

### Changed
- **Server**: Manager persistence integration now writes bot profiles and matches to SQLite on lifecycle transitions
- **Server**: Bootstrap flow now performs server identity and global registration with durable startup state
- **Server**: Dashboard template loading moved to configurable runtime path resolution
- **Server**: Plugin discovery now resolves from `BBS_GAME_PLUGIN_DIR` or runtime defaults

### Infrastructure
- **Infrastructure**: Added .deb package structure with systemd service, user setup, and post-install scripts
- **Docs**: Added deployment guide at `docs/deployment/LINUX_PACKAGING_PROFILE.md`
- **Docs**: Added architecture decision record at `docs/architecture/ADR_RUNTIME_PATHS_STAGE0.md`
- **Build**: Added Makefile `deb` target for Linux package builds

### Fixed
- **Server**: Template loading no longer depends on process working directory
- **Server**: Plugin discovery behavior is consistent between development and packaged installs

## [v0.2.0] - 2026-03-20

### Added
- **Infrastructure**: Formalized release management workflow, versioning approach, and Makefile build targets
- **Infrastructure**: Added comprehensive `.gitignore` coverage for binaries, build artifacts, LaTeX output, and credentials
- **Docs**: Added contributor guide in `CONTRIBUTING.md`
- **Docs**: Added installation-path documentation (source, pre-built, direct run)
- **Docs**: Clarified language-agnostic plugin support

### Changed
- **Docs**: Breaking documentation update to emphasize language-neutral RPC contract over Go-specific patterns
- **Docs**: Improved plugin authoring guidance for language flexibility
- **Docs**: Updated README installation section with three options and release references

### Fixed
- **Server**: Deadlock conditions during concurrent bot registration
- **Server**: Stale dashboard state display issues
- **Server**: Dashboard state synchronization reliability

### Infrastructure
- **Build**: Added Makefile targets for build, test, lint, and release workflows
- **Docs**: Added `VERSIONING.md` and initial `CHANGELOG.md`
- **Infrastructure**: Hardened `.gitignore` coverage for binaries, transient files, and secrets
- **Infrastructure**: Added GitHub Actions automation for build and release publishing
- **Docs**: Added local command-cheat-sheet pattern (`.local.md`)

## [v0.1.0] - 2026-03-19

### Added
- **Server**: Plugin-only architecture; built-in games removed in favor of process-plugin runtime
- **Plugins**: Added Gridworld RL reference plugin with episodic support and configurable rewards
- **Viewer**: Introduced decoupled viewer model with plugin-provided JavaScript bundles
- **Docs**: Added reference plugin implementations for Go and Python
- **Examples**: Added `python_gridworld_q_bot.py` as a learning-agent example

### Changed
- **Server**: Breaking removal of built-in Connect4, Chess, Checkers, and Gridworld games
- **Viewer**: Breaking removal of server-side viewer rendering; rendering moved to client/plugin side
- **Server**: Dashboard game discovery now scans plugin manifests from `cmd/bbs-server/plugins/games/`
- **Plugins**: Plugin manifest now requires `viewer_client_entry` JavaScript bundle reference

### Deprecated
- **Viewer API**: Server-side viewer methods (`GetViewerSpec`, `GetViewerFrame`) deprecated in favor of client-side rendering

### Fixed
- **Server**: Plugin loading timing and runtime reliability
- **Dashboard**: State consistency during rapid game creation

### Internal
- **Server**: Refactored games subsystem around plugin host pattern
- **Server**: Improved plugin process lifecycle management
- **Protocol**: Added shared process-plugin RPC contract at `games/pluginapi/`

## [v0.0.2] - 2026-03-19

### Added
- **Runtime**: `BBS_ENABLE_GAME_PLUGINS` now propagates to bot processes
- **Game Engine**: Added support for autonomous-mode games (`RequiredPlayers() == 0`)

### Changed
- **Plugins**: Plugin manifest discovery now handles version field correctly

### Fixed
- **Runtime**: Fixed plugin environment variable inheritance

## [v0.0.1] - 2026-03-19

### Added
- **Release**: Initial release of Build-a-Bot Stadium
- **Core Platform**: TCP server and HTTP dashboard for arena management
- **Games**: Added Connect4, Gridworld, Checkers, and Chess runtime support
- **Plugins**: Early process-plugin support with Counter reference plugin
- **Dashboard**: Web arena management and live viewer capabilities
- **Agent**: Added `bbs-agent` local JSONL bridge for bot developers
- **Deployment**: Added TrueNAS Docker and native deployment scripts
- **Protocol**: Added TCP command/response protocol surface
- **Session Management**: Added dynamic session and arena lifecycle support
- **Identity**: Added bot identity and ownership token model
- **Gameplay**: Added move clock, handicap support, and match replay
- **Manifest**: Added game argument schema support in manifests

---

## Component Versions

This section records the latest released component tags.
Update these entries only when component-specific tags are cut.

### bbs-agent

- **v0.2.0**: Improved error handling and socket management
- **v0.1.0**: Initial local bridge implementation with Unix socket support

### bbs-game-counter-plugin

- **v0.1.0**: Reference Go plugin implementation

### bbs-plugin-manifest-lint

- **v0.1.0**: Manifest validation tool

---

## Version Compatibility

### Current Stable

- **Repository**: v0.5.0
- **TCP Protocol**: Stable; new command types are additive
- **Plugin RPC**: Protocol v1 (stable)
- **Agent Contract**: v0.2 (pre-v1.0; breaking changes possible)

### Upgrading

- **v0.0.x → v0.1.0**: Requires rewriting game plugins to use plugin-only manifest format.
- **v0.3.x → v0.5.x**: Review server discovery and owner-token behavior changes before rollout.
- Client upgrades should preserve persona-backed state; validate deployment and server-access flows after upgrade.

---

## Release Strategy

See [docs/releases/VERSIONING.md](docs/releases/VERSIONING.md) for detailed versioning and release process documentation.
