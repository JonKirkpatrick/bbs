# Track A Task Checklist (Desktop Client Alpha)

This checklist turns the Track A roadmap slices into an execution plan with dependency order, estimated effort, and concrete completion checks.

How to use:
- Mark `Status` as `todo`, `in-progress`, `blocked`, or `done`.
- Keep tasks small enough to complete and validate in one pull request where practical.
- Record proof-of-completion in PR descriptions (screenshots, logs, or test output).

Estimate scale:
- `XS`: less than 0.5 day
- `S`: 0.5 to 1 day
- `M`: 1 to 3 days
- `L`: 3 to 5 days
- `XL`: more than 5 days

## Progress Snapshot (23 March 2026)

- Slices A0, A1, and A2 are complete.
- Slice A3 baseline (`A3-01` through `A3-04`) is complete.
- Remaining A3 work is visual status-glow rules (`A3-05`).
- A6 storage/identity integration coverage (`A6-01`) is complete; orchestration hardening coverage (`A6-02`) is underway.

## Milestone Order

1. A0 Foundation
2. A1 Persistence + Identity
3. A2 Unified Layout
4. A3 Bot Registration + Orchestration
5. A4 Known Servers + Probing
6. A5 Server Context + Owner-Token Actions
7. A6 Hardening + Alpha Packaging

## Checklist

| ID | Slice | Task | Estimate | Depends On | Status | Definition of done |
| --- | --- | --- | --- | --- | --- | --- |
| A0-01 | A0 | Create Avalonia solution + project structure (`App`, `Core`, `Infrastructure`) | M | - | done | Solution builds and launches app shell on Linux |
| A0-02 | A0 | Establish MVVM conventions and base view model infrastructure | S | A0-01 | done | Base view model + command patterns used by at least one screen |
| A0-03 | A0 | Define client domain models (`ClientIdentity`, `BotProfile`, `KnownServer`, `ServerPluginCache`, `AgentRuntimeState`) | S | A0-01 | done | Models compile, are serializable/mappable, and have basic validation |
| A0-04 | A0 | Add local logging/telemetry abstraction for app runtime diagnostics | S | A0-01 | done | App writes structured local logs with levels and timestamps |
| A1-01 | A1 | Implement storage abstraction interfaces for client persistence | S | A0-03 | done | Storage contracts support identity, bots, servers, plugin cache, runtime state |
| A1-02 | A1 | Implement SQLite-backed storage provider and startup initialization | M | A1-01 | done | DB file is created automatically and startup does not require manual steps |
| A1-03 | A1 | Add schema version table + migration runner | M | A1-02 | done | Migration path runs idempotently and reports current schema version |
| A1-04 | A1 | Implement first-launch client identity bootstrap + durable save | S | A1-02 | done | First launch creates identity, restart loads same identity |
| A2-01 | A2 | Build unified main workspace view (left panel, center host, right panel) | M | A0-02, A1-02 | done | App launches directly into unified workspace |
| A2-02 | A2 | Implement collapsible behavior for left and right panels | S | A2-01 | done | Both panels can collapse/expand and layout remains stable |
| A2-03 | A2 | Implement context host navigation model for center activity area | M | A2-01 | done | Selecting contexts swaps center content predictably |
| A2-04 | A2 | Create reusable card components for bot/server entries with status style hooks | S | A2-01 | done | Card component supports title, metadata, status style, click/select |
| A3-01 | A3 | Implement bot registration/edit form in center activity area | M | A2-03, A1-02 | done | User can add and edit bot path/args/metadata |
| A3-02 | A3 | Persist bot profiles and display them in left panel cards | S | A3-01 | done | Bot list survives restart and renders from storage |
| A3-03 | A3 | Implement arm/disarm orchestration service for bot+agent lifecycle | L | A3-02 | done | Arm launches processes and disarm stops them with clear result state |
| A3-04 | A3 | Wire control-channel status/lifecycle updates into bot runtime state model | M | A3-03 | done | Runtime updates reflected in state store and visible in UI |
| A3-05 | A3 | Apply bot card glow rules (amber armed, green active session, red error) | S | A3-04, A2-04 | todo | Glows match state transitions and update in near-real time |
| A4-01 | A4 | Implement known server registration/edit flow in center activity area | M | A2-03, A1-02 | todo | User can add/edit server records with IDs and endpoints |
| A4-02 | A4 | Persist known server records + cached plugin snapshots | M | A4-01 | todo | Server and plugin cache state survives restart |
| A4-03 | A4 | Implement startup probe loop for known servers | M | A4-02 | todo | Startup probe marks servers reachable/unreachable with timeout policy |
| A4-04 | A4 | Apply server card visual states (green live, grey inactive) | S | A4-03, A2-04 | todo | Card styling matches latest probe result |
| A4-05 | A4 | Add manual refresh/reprobe action for known servers | S | A4-03 | todo | User can trigger reprobe and see updated status |
| A5-01 | A5 | Build server detail view in center activity area | M | A4-02, A2-03 | todo | Selecting a server loads details view with cached metadata |
| A5-02 | A5 | Retrieve and display agent `server_access` metadata (owner token + dashboard endpoint) | M | A3-04, A5-01 | todo | Access metadata shown for armed bot sessions and refreshable |
| A5-03 | A5 | Add owner-token-gated action stubs (create/join arena command path placeholders) | M | A5-02 | todo | Gated actions appear only when session metadata is valid |
| A5-04 | A5 | Add server plugin catalog viewer pane in center activity area | S | A5-01, A4-02 | todo | Cached plugin data is readable in UI |
| A6-01 | A6 | Add integration tests for first-launch identity + storage migration | M | A1-03, A1-04 | done | Tests validate initialization and schema progression behavior |
| A6-02 | A6 | Add integration tests for arm/disarm/lifecycle/quit orchestration states | M | A3-04 | in-progress | Tests cover expected transitions and failure handling |
| A6-03 | A6 | Add resilient error handling for stale process handles and socket failures | M | A3-03 | todo | User-visible errors are clear and app recovers without restart |
| A6-04 | A6 | Document Linux build/run packaging and local development workflow | S | A2-01, A6-02 | todo | Docs allow clean setup and launch on a fresh Linux environment |
| A6-05 | A6 | Alpha readiness pass (UX sanity, data durability checks, smoke checklist) | M | A6-01, A6-02, A6-04 | todo | Internal alpha sign-off checklist completed |

## Dependency Notes

- `A3` should not begin before `A1` storage contracts stabilize, to avoid rework in runtime-state persistence.
- `A5` depends on `A3` control-channel and runtime integration for owner-token-aware behavior.
- `A6` closes gaps and should include regression checks before first public/internal alpha distribution.

## Suggested Execution Rhythm

- Sprint 1: `A0` + `A1`
- Sprint 2: `A2` + core of `A3`
- Sprint 3: finish `A3` + `A4`
- Sprint 4: `A5` + `A6` hardening

## Sprint 1 Cut (A0 + A1)

Objective:
- Deliver a working Avalonia shell plus durable client identity + SQLite-backed persistence bootstrap in small, reviewable PRs.

Execution order:
1. PR1 Foundation skeleton
2. PR2 Domain model contracts
3. PR3 Storage abstraction + SQLite bootstrap
4. PR4 First-launch identity + migrations

### PR1 Foundation skeleton

- Suggested branch: `track-a/s1-pr1-foundation-shell`
- Suggested PR title: `Track A S1/PR1: Avalonia foundation and app shell`
- Task mapping:
	- `A0-01`
	- `A0-02`
	- `A0-04` (initial minimal logger only)
- Completion checks:
	- Solution builds on Linux.
	- App launches and shows shell window.
	- Basic structured log lines are emitted on startup/shutdown.
- Evidence to attach:
	- Build output snippet.
	- Screenshot of shell window.
	- Sample log excerpt.

### PR2 Domain model contracts

- Suggested branch: `track-a/s1-pr2-domain-models`
- Suggested PR title: `Track A S1/PR2: Client domain model contracts`
- Task mapping:
	- `A0-03`
- Completion checks:
	- Core models compile and are documented.
	- Validation rules are enforced for required fields.
	- Model serialization round-trip tests pass.
- Evidence to attach:
	- Unit test output for model tests.
	- Short schema/model summary in PR description.

### PR3 Storage abstraction + SQLite bootstrap

- Suggested branch: `track-a/s1-pr3-storage-sqlite-bootstrap`
- Suggested PR title: `Track A S1/PR3: Storage abstraction and SQLite initialization`
- Task mapping:
	- `A1-01`
	- `A1-02`
- Completion checks:
	- Storage interfaces cover identity, bots, servers, plugin cache, runtime state.
	- SQLite DB file is created automatically on app start.
	- Startup does not require manual DB setup.
- Evidence to attach:
	- Integration or startup test output.
	- Local run notes with DB path confirmation.

### PR4 First-launch identity + migrations

- Suggested branch: `track-a/s1-pr4-identity-migrations`
- Suggested PR title: `Track A S1/PR4: First-launch client identity and schema migrations`
- Task mapping:
	- `A1-03`
	- `A1-04`
- Completion checks:
	- Schema version table exists and migration runner is idempotent.
	- First launch creates persistent identity.
	- Relaunch preserves same identity.
- Evidence to attach:
	- Migration test output.
	- Identity persistence test output.

Sprint 1 exit criteria:
- PR1 through PR4 merged.
- Tasks `A0-01` to `A1-04` marked `done`.
- Linux setup/build/run notes are updated for new contributors.

## Sprint 2 Cut (A2 + Core A3)

Objective:
- Deliver the unified single-view workspace with collapsible panels and context host, then land the first bot registration + persistence flow.

Execution order:
1. PR1 Unified workspace shell
2. PR2 Panel behavior and center context routing
3. PR3 Bot registration + persistence wiring
4. PR4 Orchestration service baseline

### PR1 Unified workspace shell

- Suggested branch: `track-a/s2-pr1-unified-workspace-shell`
- Suggested PR title: `Track A S2/PR1: Unified workspace shell layout`
- Task mapping:
	- `A2-01`
- Completion checks:
	- App launches directly into unified workspace.
	- Left bot panel, center host, and right server panel are visible in one view.
	- View model and view composition are wired without placeholder startup errors.
- Evidence to attach:
	- Screenshot showing full three-region layout.
	- Build output snippet.

### PR2 Panel behavior and center context routing

- Suggested branch: `track-a/s2-pr2-panels-and-context-routing`
- Suggested PR title: `Track A S2/PR2: Collapsible panels and context host routing`
- Task mapping:
	- `A2-02`
	- `A2-03`
	- `A2-04`
- Completion checks:
	- Left and right panels collapse and expand cleanly.
	- Context selection swaps center content predictably.
	- Reusable card components render title, metadata, and status hooks.
- Evidence to attach:
	- Short screen recording or image sequence of collapse/expand behavior.
	- Unit test output for routing/card view model behavior if present.

### PR3 Bot registration + persistence wiring

- Suggested branch: `track-a/s2-pr3-bot-registration-persistence`
- Suggested PR title: `Track A S2/PR3: Bot registration flow and durable bot profiles`
- Task mapping:
	- `A3-01`
	- `A3-02`
- Completion checks:
	- Bot registration/edit flow is available in center activity area.
	- Bot profiles persist through restart.
	- Left panel reflects stored bot entries on launch.
- Evidence to attach:
	- Test output for profile persistence.
	- Before/after restart screenshot showing retained bot cards.

### PR4 Orchestration service baseline

- Suggested branch: `track-a/s2-pr4-orchestration-baseline`
- Suggested PR title: `Track A S2/PR4: Arm/disarm orchestration baseline and lifecycle state`
- Task mapping:
	- `A3-03` (baseline path)
	- `A3-04` (initial state propagation)
- Completion checks:
	- Arm/disarm service path is implemented for at least one bot profile.
	- Lifecycle state transitions are captured in runtime state model.
	- UI can surface lifecycle state changes without app restart.
- Evidence to attach:
	- Integration test output for arm/disarm transitions.
	- Runtime log excerpt with lifecycle events.

Sprint 2 exit criteria:
- PR1 through PR4 merged.
- Tasks `A2-01` to `A2-04`, `A3-01`, and `A3-02` marked `done`.
- `A3-03` and `A3-04` baseline implementation merged and validated.

## Sprint 3 Cut (Finish A3 + A4)

Objective:
- Complete bot glow semantics and deliver known-server management with probing, cache persistence, and reprobe UX in small, reviewable PRs.

Execution order:
1. PR1 Bot runtime glow-state completion
2. PR2 Known server CRUD + persistence cache
3. PR3 Startup probing pipeline
4. PR4 Server visual status + manual reprobe

### PR1 Bot runtime glow-state completion

- Suggested branch: `track-a/s3-pr1-bot-glow-states`
- Suggested PR title: `Track A S3/PR1: Complete bot glow-state semantics`
- Task mapping:
	- `A3-05`
- Completion checks:
	- Bot cards show amber while armed/idle, green during active session, and red on error conditions.
	- Glow transitions follow runtime state updates without requiring app restart.
	- Visual rules are covered by view model/state mapping tests where feasible.
- Evidence to attach:
	- Short screen capture or screenshot sequence showing state transitions.
	- Test output for glow-state mapping or relevant runtime-state tests.

### PR2 Known server CRUD + persistence cache

- Suggested branch: `track-a/s3-pr2-known-server-persistence`
- Suggested PR title: `Track A S3/PR2: Known server registration and durable cache`
- Task mapping:
	- `A4-01`
	- `A4-02`
- Completion checks:
	- User can add/edit known servers in the center activity flow.
	- Known server records persist across app restart.
	- Plugin snapshot/cache model persists and reloads correctly.
- Evidence to attach:
	- Test output for server persistence and cache reload behavior.
	- Before/after restart screenshot confirming retained server entries.

### PR3 Startup probing pipeline

- Suggested branch: `track-a/s3-pr3-startup-server-probe`
- Suggested PR title: `Track A S3/PR3: Startup probe loop for known servers`
- Task mapping:
	- `A4-03`
- Completion checks:
	- Startup probe executes for known endpoints with timeout/backoff policy.
	- Reachable/unreachable status updates are stored in runtime/persistent state as designed.
	- Probe failures do not block app startup or crash UI.
- Evidence to attach:
	- Probe log excerpt with at least one success and one timeout/failure path.
	- Integration or component test output for probing behavior.

### PR4 Server visual status + manual reprobe

- Suggested branch: `track-a/s3-pr4-server-status-and-reprobe`
- Suggested PR title: `Track A S3/PR4: Server card status rendering and manual reprobe`
- Task mapping:
	- `A4-04`
	- `A4-05`
- Completion checks:
	- Server cards render green for live and grey for inactive/offline states.
	- User can trigger manual refresh/reprobe and observe status update.
	- Reprobe action is safe against concurrent triggers and stale status display.
- Evidence to attach:
	- UI screenshot showing live/offline card state variants.
	- Test or manual validation notes demonstrating reprobe updates.

Sprint 3 exit criteria:
- PR1 through PR4 merged.
- Tasks `A3-05` and `A4-01` through `A4-05` marked `done`.
- Known-server state and probing workflow validated on a clean restart.

## Sprint 4 Cut (A5 + A6 Hardening)

Objective:
- Deliver server-context actions and harden the alpha path with integration coverage, lifecycle resiliency, and packaging/readiness documentation.

Execution order:
1. PR1 Server detail context and plugin catalog view
2. PR2 Session access metadata in server context
3. PR3 Owner-token-gated action stubs
4. PR4 Orchestration hardening and integration coverage
5. PR5 Packaging docs and alpha readiness pass

### PR1 Server detail context and plugin catalog view

- Suggested branch: `track-a/s4-pr1-server-detail-catalog`
- Suggested PR title: `Track A S4/PR1: Server detail context and plugin catalog viewer`
- Task mapping:
	- `A5-01`
	- `A5-04`
- Completion checks:
	- Selecting a server opens a detail view in center context with cached metadata.
	- Plugin catalog pane renders cached plugin data with stable empty/loading states.
	- View model contracts are test-covered for selection-to-context behavior.
- Evidence to attach:
	- Screenshot of server detail view and plugin catalog pane.
	- Test output for context selection/view model behavior.

### PR2 Session access metadata in server context

- Suggested branch: `track-a/s4-pr2-server-access-metadata`
- Suggested PR title: `Track A S4/PR2: Surface server access metadata from armed sessions`
- Task mapping:
	- `A5-02`
- Completion checks:
	- Active armed sessions can retrieve and display `server_access` metadata.
	- Owner token and dashboard endpoint fields are surfaced with clear validity states.
	- Metadata refresh path is non-blocking and error-tolerant.
- Evidence to attach:
	- Runtime log excerpt of control-channel metadata fetch.
	- UI screenshot showing populated server access metadata.

### PR3 Owner-token-gated action stubs

- Suggested branch: `track-a/s4-pr3-owner-token-gated-actions`
- Suggested PR title: `Track A S4/PR3: Owner-token-gated action stubs in server context`
- Task mapping:
	- `A5-03`
- Completion checks:
	- Owner-token-gated action placeholders appear only when metadata/token is valid.
	- Action trigger path validates preconditions and reports friendly failure states.
	- Command wiring is structured for future create/join arena implementation.
- Evidence to attach:
	- Screenshot of gated action visibility on/off conditions.
	- Test output for gating logic and precondition checks.

### PR4 Orchestration hardening and integration coverage

- Suggested branch: `track-a/s4-pr4-hardening-and-integration`
- Suggested PR title: `Track A S4/PR4: Orchestration resiliency and integration test completion`
- Task mapping:
	- `A6-02`
	- `A6-03`
- Completion checks:
	- Integration tests cover arm/disarm/lifecycle/quit transitions including failure paths.
	- Stale process handles and socket failures are surfaced with recoverable UX behavior.
	- Failure handling does not require app restart to continue normal operation.
- Evidence to attach:
	- Integration test output showing transition matrix coverage.
	- Log excerpt of handled stale-process or socket-failure recovery path.

### PR5 Packaging docs and alpha readiness pass

- Suggested branch: `track-a/s4-pr5-packaging-and-alpha-readiness`
- Suggested PR title: `Track A S4/PR5: Linux packaging docs and alpha readiness checklist`
- Task mapping:
	- `A6-04`
	- `A6-05`
- Completion checks:
	- Linux build/run/developer workflow docs are complete and reproducible on clean setup.
	- Alpha sanity checklist (UX, persistence durability, smoke checks) is completed.
	- Remaining launch blockers are documented with owners and severity.
- Evidence to attach:
	- Fresh-environment setup notes and command transcript.
	- Completed alpha checklist artifact in PR description.

Sprint 4 exit criteria:
- PR1 through PR5 merged.
- Tasks `A5-01` through `A5-04` and `A6-02` through `A6-05` marked `done`.
- Alpha release candidate passes documented smoke and durability checks.
