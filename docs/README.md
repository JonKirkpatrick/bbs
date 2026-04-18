# Documentation Index

This directory contains project documentation organized by purpose.

## Architecture

- [ARCHITECTURE.md](architecture/ARCHITECTURE.md): Runtime/server decomposition and design boundaries.
- [CLIENT_APP_ARCHITECTURE.md](architecture/CLIENT_APP_ARCHITECTURE.md): Desktop client structure, startup flow, and service viewmodel boundaries.

## Reference

- [PROTOCOL.md](reference/PROTOCOL.md): TCP bot protocol commands and envelope behavior.
- [BBS_AGENT_CONTRACT.md](reference/BBS_AGENT_CONTRACT.md): Local JSONL bridge contract used by `bbs-agent`.
- [TRUST_PROFILE_V1.md](reference/TRUST_PROFILE_V1.md): Security/trust baseline for TLS, ephemeral agent identity, and capability-scoped authorization.
- [TRUST_PROFILE_V1_IMPLEMENTATION_CHECKLIST.md](reference/TRUST_PROFILE_V1_IMPLEMENTATION_CHECKLIST.md): Step-by-step implementation checklist mapping trust profile goals to code and protocol touchpoints.

## Guides

- [PLUGIN_AUTHORING.md](guides/PLUGIN_AUTHORING.md): Manifest schema, plugin workflow, and release checklist.
- [DASHBOARD_REMOTE_QUICKSTART.md](guides/DASHBOARD_REMOTE_QUICKSTART.md): Remote dashboard/operator setup and usage.

## Releases

- [VERSIONING.md](releases/VERSIONING.md): Semantic versioning and release strategy.
- [ROADMAP.md](releases/ROADMAP.md): Planned future direction.
- Root-level [CHANGELOG.md](https://github.com/JonKirkpatrick/bbs/blob/main/CHANGELOG.md): Released changes.

## Current Client Notes

- The desktop client uses deploy-based runtime instances for active sessions.
- Server context resolves owner-token metadata from the selected known server profile first.
- Bot-context JOIN dropdowns are populated from the selected server's active arena list.

## Local Working Notes

The `local/` folder contains local/private workflow notes.

- [MAKE_CHEATSHEET.local.md](local/MAKE_CHEATSHEET.local.md)
- [TESTING.local.md](local/TESTING.local.md)
- [RELEASE_TRACK_C.local.md](local/RELEASE_TRACK_C.local.md)
- [RELEASE_v0.2.0.local.md](local/RELEASE_v0.2.0.local.md)
