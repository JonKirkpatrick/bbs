# Changelog

All notable changes to Build-a-Bot Stadium are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Added
(new changes will be captured here)

### Changed
(changes will be captured here)

### Fixed
(fixes will be captured here)

## [v0.3.0] - 2026-03-22

### Added
- **SQLite Persistence (Stage 0)**: Durable storage for server identity, match history, bot profiles, and federation outbox
- **Runtime Path Contract**: Configurable paths for templates, plugins, config, and data with environment overrides
- **Linux FHS Packaging Profile**: Native `.deb` package support with systemd integration (via `BBS_PACKAGING_MODE=linux-fhs`)
- **Federation Infrastructure**: Outbox worker with retry/backoff, HTTP publisher, mock global registrar, and dedupe receipts
- **Admin Debug Endpoints**: `/admin/debug/server-identity`, `/admin/debug/outbox`, `/admin/debug/recent-matches` for operational visibility
- **Mock Federation Receiver**: Optional token-gated `/federation/mock/ingest` endpoint for loopback testing

### Changed
- **Persistence Integration**: Manager now persists bot profiles and matches to SQLite on lifecycle events
- **Bootstrap Flow**: Server identity and global registration now happen at startup with durable state
- **Template Loading**: Dashboard templates resolved from configurable path instead of hardcoded relative path
- **Plugin Discovery**: Plugin directory resolved from `BBS_GAME_PLUGIN_DIR` or runtime defaults (dev-friendly fallbacks)

### Infrastructure
- Added `.deb` package structure with systemd service, user setup, and post-install scripts
- Added `docs/deployment/LINUX_PACKAGING_PROFILE.md` with `.deb` build and deployment examples
- Added `docs/architecture/ADR_RUNTIME_PATHS_STAGE0.md` documenting path contract decisions
- Makefile `deb` target for building `.deb` packages on Linux

### Fixed
- Template loading no longer depends on working directory
- Plugin discovery is consistent across development and packaged installations

## [v0.2.0] - 2026-03-20

### Added
- **Release Management Infrastructure**: Formalized versioning, Makefile build targets, and release workflow
- **Repository Hygiene**: Comprehensive `.gitignore` excluding binaries, build artifacts, LaTeX files, and credentials
- **Contributor Guide**: `CONTRIBUTING.md` with development setup, testing, and plugin authoring workflows
- **Installation Options**: Multiple installation paths documented (from source, pre-built, direct run)
- **Language-Agnostic Documentation**: Clarified that plugins can be written in any language (Go, Python, Rust, etc.)

### Changed
- **Breaking**: Plugin documentation now emphasizes language-neutral RPC contract over Go-specific patterns
- Improved wording in plugin authoring guide to clearly state language flexibility
- Updated README installation section with three options and release references

### Fixed
- Deadlock conditions during concurrent bot registration
- Stale state display issues in dashboard views
- Dashboard state synchronization improvements

### Infrastructure
- Added `Makefile` with convenient build, test, lint, and release targets
- Added `VERSIONING.md` with semantic versioning strategy and release process
- Added `CHANGELOG.md` changelog file
- Established `.gitignore` to exclude binaries, transient files, and credentials
- GitHub Actions CI automation for building and publishing releases
- Command cheat sheet support (`.local.md` files for personal notes)

## [v0.1.0] - 2026-03-19

### Added
- **Plugin-Only Architecture**: Removed all built-in games; now all games are provided as process plugins
- **Gridworld RL Plugin**: New Python-based single-player environment with episodic support, configurable rewards, and replay support
- **Enhanced Viewer**: Decoupled viewer rendering from server internals; plugins now provide custom JavaScript UI bundles
- **Improved Documentation**: Added reference implementations for Go and Python plugins
- **Q-Learning Bot Example**: New `python_gridworld_q_bot.py` demonstrating learning agent patterns

### Changed
- **Breaking**: Removed built-in Connect4, Chess, Checkers, and Gridworld games
- **Breaking**: Removed server-side viewer rendering; all rendering is now client-side plugin code
- Dashboard game discovery now scans manifest files from `cmd/bbs-server/plugins/games/`
- Plugin manifest now requires `viewer_client_entry` field pointing to JavaScript bundle

### Deprecated
- Server-side viewer methods (`GetViewerSpec`, `GetViewerFrame`) replaced by client-side rendering

### Fixed
- Plugin loading timing and reliability
- Dashboard state consistency during rapid game creation

### Internal
- Completely refactored games subsystem to use plugin host pattern
- Improved plugin process lifecycle management
- New `games/pluginapi/` shared protocol for process-based plugins

## [v0.0.2] - 2026-03-19

### Added
- Environment variable export: `BBS_ENABLE_GAME_PLUGINS` now propagates to bot processes
- Support for autonomous-mode games (`RequiredPlayers() == 0`)

### Changed
- Plugin manifest discovery now handles version field correctly

### Fixed
- Plugin environment variable inheritance

## [v0.0.1] - 2026-03-19

### Added
- Initial release of Build-a-Bot Stadium
- **Core Platform**: TCP server and HTTP dashboard for managing bot arenas
- **Game Support**:
  - Connect4 (two-player)
  - Gridworld (single-player environment)
  - Checkers (two-player)
  - Chess (two-player)
- **Plugin System**: Early-stage process plugin support with Counter reference plugin
- **Dashboard**: Web-based arena management and live viewer
- **Agent Bridge**: `bbs-agent` local JSONL bridge for bot developers
- **Deployment Scripts**: TrueNAS Docker and native deployment helpers

### Features
- TCP protocol for bot command/response
- Dynamic session and arena management
- Bot identity and ownership tokens
- Move clock and handicap support
- Match replay capability
- Game argument schema in manifests

---

## Component Versions

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

- **Repository**: v0.1.0 (plugin-only architecture)
- **TCP Protocol**: Stable; new command types are additive
- **Plugin RPC**: Protocol v1 (stable)
- **Agent Contract**: v0.2 (pre-v1.0; breaking changes possible)

### Upgrading

- **v0.0.x → v0.1.0**: Requires rewriting game plugins to use new plugin-only manifest format
- All components: No database migration needed (in-memory state)

---

## Release Strategy

See [docs/releases/VERSIONING.md](docs/releases/VERSIONING.md) for detailed versioning and release process documentation.
