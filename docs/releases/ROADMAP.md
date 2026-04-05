# Build-a-Bot Stadium Roadmap

This roadmap describes near-term priorities and direction for upcoming releases.

It is intentionally lightweight and subject to change as implementation details are validated.

## Vision

Build-a-Bot Stadium should be a practical platform for running bots in process-plugin-driven arenas, with clear operational tooling and reliable persistence.

Near-term work focuses on:

- an alpha desktop client for bot-centric orchestration and server discovery/interaction
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

Current status snapshot (23 March 2026):
- Completed: A0, A1, A2, and A3 baseline (`A3-01` through `A3-04`).
- In progress: `A3-05` bot glow-state semantics and later A4-A6 slices.
- Tracking source of truth: `docs/releases/TRACK_A_TASK_CHECKLIST.md`.

Goal:
- Introduce a local desktop client that manages bots as first-class entities and orchestrates agent+bot runtime transparently.

Scope:
- Establish a persistent client identity on first launch (local ID now, global/federated ID-compatible model).
- Introduce persistent local storage for client state (SQLite preferred default; lightweight fallback optional only if SQLite proves unnecessary).
- Launch directly into a unified workspace view (no top-level mode switching).
- Include a collapsible left panel for bots and a collapsible right panel for servers.
- Use the center activity area as a context-driven workspace that loads bot/server-specific flows.
- Bot panel scope:
	- render one card per registered bot,
	- include entry point to register a new bot,
	- show glanceable status glow states on cards (amber=attached, green=active session, red=error).
- Server panel scope:
	- render one card per known server,
	- probe cached servers on startup to refresh availability,
	- show glanceable server status (green=live, grey=inactive/offline).
- Center activity scope:
	- load bot registration and bot metadata/editor flows,
	- load server detail views when a server is selected,
	- expose owner-token-gated commands for active sessions on selected server (for example create/join arena actions).

Definition of done:
- First launch initializes durable client identity and persistent storage automatically.
- User can register at least one bot profile and launch/detach it from the GUI, with agent lifecycle handled by the client.
- Attached bot flow can retrieve server access metadata through the agent control channel (owner token + dashboard endpoint).
- Known server records persist across restarts, including cached plugin catalog snapshots.
- Unified single-view layout is functional with collapsible bot/server side panels and context-driven center workspace.
- Bot and server cards expose the planned alpha status indicators (bot: amber/green/red, server: green/grey).
- Startup server probing updates known server availability state in the UI.
- Selecting bot/server cards loads appropriate context views in the center activity area.
- Active-session server views can surface owner-token-gated actions in the center workspace.
- Alpha build/run instructions documented for Linux desktop environment.

Likely follow-on:
- Per-bot global identity strategy, cross-server credential lifecycle policies, and richer federated server resolution behavior.

Implementation slices (Track A alpha):

Execution checklist reference:
- `docs/releases/TRACK_A_TASK_CHECKLIST.md`

Slice A0: Foundation and project skeleton
- Create Avalonia solution skeleton, baseline MVVM structure, and app shell.
- Define core client domain models (`ClientIdentity`, `BotProfile`, `KnownServer`, `ServerPluginCache`, `AgentRuntimeState`).
- Add lightweight telemetry/logging abstraction for local diagnostics.
- Checkpoint outcome: app launches to shell, no persistence yet, architecture and model contracts established.

Slice A1: Persistence and first-launch identity
- Introduce storage layer with SQLite as default implementation.
- Implement first-launch initialization flow for durable client identity.
- Add schema versioning and startup migration path for client DB.
- Checkpoint outcome: identity and empty collections persist across restarts.

Slice A2: Unified layout and panel infrastructure
- Build unified workspace UI with collapsible left bot panel and right server panel.
- Implement center activity host and navigation/context routing model.
- Add card components for bot/server lists with status rendering hooks.
- Checkpoint outcome: static panel interactions work and center area swaps context views.

Slice A3: Bot registration and orchestration wiring
- Implement bot registration/edit form in center activity area.
- Persist bot metadata including launch path and args.
- Implement launch/detach orchestration path that launches/monitors agent+bot transparently.
- Map orchestration status to bot card glow states (amber/green/red).
- Checkpoint outcome: at least one bot can be registered, attached, detached, and status-reflected in UI.

Slice A4: Known server management and probing
- Implement known server registration/edit workflow and persistence.
- Add startup/server-list probe cycle for cached endpoints.
- Cache server metadata and plugin catalog snapshots on successful contact.
- Render server card status (green/grey) from probe state.
- Checkpoint outcome: server list is persistent and availability updates at boot and refresh.

Slice A5: Context server view and owner-token actions (alpha)
- Implement server detail view in center activity area.
- Wire agent control channel retrieval of server access metadata (`owner_token`, dashboard endpoint).
- Expose first owner-token-gated action stubs/flows (for example create/join arena command paths).
- Checkpoint outcome: selected active server view can show gated controls for an attached bot session.

Slice A6: Hardening and alpha packaging
- Add integration tests for orchestration and storage initialization paths.
- Add guardrails for process lifecycle errors and stale connection states.
- Finalize Linux build/run packaging notes and developer setup documentation.
- Checkpoint outcome: alpha path is documented, repeatable, and stable enough for internal usage.

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
