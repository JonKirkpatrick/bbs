# Build-a-Bot Stadium Roadmap

This roadmap describes near-term priorities and direction for upcoming releases.

It is intentionally lightweight and subject to change as implementation details are validated.

## Vision

Build-a-Bot Stadium should be a practical platform for running bots in process-plugin-driven arenas, with clear operational tooling and reliable persistence.

Near-term work focuses on:

- an alpha desktop client for local bot/agent operations
- persistent server-side storage with SQLite
- meaningful unit test coverage for core components

## Planning Principles

- Prioritize stability and operability over feature volume.
- Prefer incremental changes that can ship in small releases.
- Keep plugin authoring language-agnostic.
- Explicitly document what is in scope and out of scope each release.

## Current State

- Plugin-only game runtime architecture is in place.
- Dashboard and viewer plugin integration are in place.
- Basic release process and project hygiene are now formalized.

## Near-Term Release Focus

## Release Track A: Desktop Client (Alpha)

Goal:
- Introduce a local desktop client focused on managing client-side bot operations.

Scope:
- Launch, monitor, and stop `bbs-agent` processes.
- Show per-agent status (running, connected, idle/busy).
- Show live communication/log feed for each tracked agent.
- Allow guarded manual command injection for safe commands (`JOIN`, `LEAVE`, `QUIT`) when appropriate.

Definition of done:
- User can create at least one agent profile and start/stop it from the GUI.
- Client displays real-time status and logs for active agents.
- Manual commands can be sent and are visibly logged with success/failure response.
- Alpha build instructions documented for Linux.

Likely follow-on:
- Launch bots from client profiles and bind to selected agent endpoint.

## Release Track B: Server Persistence (SQLite)

Goal:
- Move key runtime entities from memory-only behavior to durable storage.

Scope:
- Introduce SQLite-backed persistence layer.
- Persist core entities first (sessions/bot identity metadata, arenas, match history, and relevant snapshots/events).
- Define migration strategy and schema versioning approach.
- Preserve current behavior where possible behind a repository/storage abstraction.

Definition of done:
- Server restart no longer loses selected persisted entities.
- Storage schema is versioned and initialization is automated.
- Read/write paths for persisted entities are covered by tests.
- Operational docs describe DB file location, backup expectations, and migration behavior.

Likely follow-on:
- Optional retention and pruning policies for large event/snapshot tables.

## Release Track C: Unit Test Expansion

Goal:
- Increase confidence in core behavior and reduce regression risk.

Scope:
- Add and organize unit tests around existing components.
- Prioritize stadium managers, protocol/contract parsing, plugin registry/host behavior, and storage abstraction.
- Introduce test fixtures/helpers to reduce setup duplication.
- Make tests easy to run in CI and locally.

Definition of done:
- Core packages have meaningful unit coverage for primary success and failure paths.
- Regression tests exist for recently fixed concurrency/state issues.
- `make test` and CI run reliably without manual setup.
- Test organization is documented for contributors.

Likely follow-on:
- Add integration tests for end-to-end agent/server/plugin interactions.

## Out of Scope (For These Near Releases)

- Full enterprise multi-tenant auth and internet-facing hardening.
- Distributed server clustering and horizontal scaling.
- Broad plugin SDKs for every language; contract-first interoperability remains the focus.
- Finalized UX polish for all client workflows beyond alpha-level operation.

## Revisit Point: Product Definition

After progress across the three release tracks above, revisit and publish:

- explicit final-product scope
- explicit non-goals/exclusions
- target user profiles and supported environments
- v1.0 readiness criteria

## How Priorities May Shift

Priorities can change based on:

- reliability issues found during testing or usage
- complexity discovered during SQLite integration
- feedback from desktop client alpha usage

When priorities shift, update this roadmap and changelog in the same commit where practical.
