# Build-a-Bot Stadium Roadmap

This roadmap describes near-term priorities and direction for upcoming releases.

It is intentionally lightweight and subject to change as implementation details are validated.

## Vision

Build-a-Bot Stadium should be a practical platform for running bots in process-plugin-driven arenas, with clear operational tooling and reliable persistence.

Near-term work focuses on:

- desktop client hardening and operability improvements
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

Status snapshot (12 April 2026):

- Desktop client alpha objectives from Track A have been delivered in the v0.5.0 cycle.
- Server-side SQLite persistence foundations are delivered.
- Documentation has moved to the Docusaurus flow and is now being normalized for versioned releases.

Track A planning docs were useful during implementation, but they are no longer the source of truth.
Current execution status should be inferred from released behavior (CHANGELOG + code), with roadmap tracking only forward-looking work.

## Completed Track: Desktop Client Alpha (Track A)

Outcome:

- Unified desktop workspace with left/center/right panel model.
- Persona-driven local workspace state.
- Bot profile registration/edit/deploy lifecycle.
- Known server registration, probing, and metadata/catalog cache usage.
- Owner-token/server-access aware context flows.
- Arena-viewer integration and packaging baseline.

Follow-on work moved into active roadmap tracks below.

## Active Track B: Server Persistence and Data Lifecycle

Goal:
- Expand persistence from foundational coverage to operationally complete lifecycle management.

Scope:
- Continue consolidating runtime entities under durable storage boundaries.
- Tighten migration discipline and backward compatibility checks.
- Define retention/pruning strategy for high-volume tables.
- Improve operational guidance for backup/restore and upgrade verification.

Definition of done:
- Restart and upgrade behavior is deterministic for persisted runtime entities.
- Schema migration tests cover expected upgrade paths.
- Runbook-level documentation exists for backup, restore, and recovery checks.
- Retention policy is defined and implemented for long-running deployments.

Likely follow-on:
- Optional export/archival tooling for historical match and event datasets.

## Active Track C: Test and Reliability Expansion

Goal:
- Raise confidence in release stability and reduce regression risk across client and server.

Scope:
- Expand high-value unit and integration coverage around orchestration, protocol handling, plugin host boundaries, and persistence migration.
- Add targeted regression tests for known state/concurrency edge cases.
- Standardize developer and CI test entry points.

Definition of done:
- Core packages have durable coverage on success/failure paths that have historically regressed.
- Recent bug fixes ship with associated regression tests.
- make test and CI runs are reproducible without local hand edits.
- Test strategy and conventions are documented for contributors.

Likely follow-on:
- Add broader end-to-end scenario tests for client to agent to server workflows.

## Active Track D: Documentation and Release Hygiene

Goal:
- Keep docs aligned with shipped behavior and make version pinning a normal release step.

Scope:
- Remove stale implementation planning docs after completion.
- Convert stable architecture knowledge into maintained architecture/reference pages.
- Align roadmap language with released milestones in CHANGELOG.
- Add a release-time docs check to catch stale references.

Definition of done:
- Docs site navigation reflects current architecture and supported workflows.
- Stale sprint/task-tracking artifacts are removed from active docs.
- Roadmap only contains forward-looking priorities.
- Release process includes docs review/versioning checkpoint.

## Out of Scope (For These Near Releases)

- Full enterprise multi-tenant auth and internet-facing hardening.
- Distributed server clustering and horizontal scaling.
- Broad plugin SDKs for every language; contract-first interoperability remains the focus.
- Finalized UX polish for all client workflows beyond the current hardening scope.

## Revisit Point: Product Definition

After progress across the active tracks above, revisit and publish:

- explicit final-product scope
- explicit non-goals/exclusions
- target user profiles and supported environments
- v1.0 readiness criteria

## How Priorities May Shift

Priorities can change based on:

- reliability issues found during testing or usage
- complexity discovered during SQLite integration
- feedback from desktop client usage

When priorities shift, update this roadmap and changelog in the same commit where practical.
