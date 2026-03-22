# ADR: SQLite Stage 0 Persistence and Federation-Ready Identity

## Status

Accepted (design baseline for initial persistence integration).

## Date

2026-03-22

## Context

The server currently keeps all runtime and historical data in memory:

- Bot identities and profile stats
- Match history and move streams
- Active sessions and live arena state

We want to begin SQLite integration in stages. Stage 0 must define what should be permanently stored and how to avoid redesign when remote server federation is added later.

Future requirements include:

- A global registry that issues canonical server identifiers
- Cross-server synchronization and deduplication
- Outbound event delivery with retries

## Decision

### 1) Durable vs transient state

Persist permanently:

- Server identity records (local and future global identity linkage)
- Bot profile identity and long-lived stats
- Match ledger and move/event history
- Federation outbound queue (outbox)
- Federation inbound dedupe receipts

Keep transient/in-memory for now:

- Active socket sessions
- Live arena runtime state while a match is in progress
- Dashboard/viewer subscriber channels and stream plumbing

### 2) Federation-ready keying

Every durable row that can be exchanged cross-server must include an `origin_server_id`.

### 3) Server identity model

Use two IDs:

- `local_server_id`: stable, locally generated, collision-resistant, available offline
- `global_server_id`: nullable initially, assigned by global registry later

`local_server_id` generation approach:

1. Generate and store a long-lived server keypair on first bootstrap.
2. Compute a fingerprint from the public key.
3. Derive `local_server_id` from the fingerprint (opaque string).

This does not claim mathematically guaranteed uniqueness without coordination, but it is practically collision-proof for operational use.

### 4) Registration conflict handling

Separate immutable identity from mutable display naming:

- Immutable identity: server IDs
- Mutable naming: preferred display name/alias

If preferred name collides, registry keeps immutable identity and returns an accepted canonical name plus optional alternatives.

### 5) Storage shape (Stage 0 conceptual)

- `server_identity`
- `bot_profiles`
- `matches`
- `match_moves`
- `federation_outbox`
- `federation_inbox_dedupe`

SQLite DDL is intentionally deferred to Stage 1 implementation to keep Stage 0 focused on contract and boundaries.

## Consequences

Positive:

- Prevents schema/ID rework when federation arrives.
- Enables incremental adoption (interfaces first, SQLite backend next).
- Establishes replay/audit-ready history model.

Trade-offs:

- Adds abstraction layer before storage engine exists.
- Requires explicit translation between in-memory runtime and durable records.

## Implementation Plan

Stage 0 (this change):

- Add ADR and architecture contracts:
  - Persistence interfaces and in-memory adapters
  - Server identity + registrar contracts
  - Mock registrar implementation for local development

Stage 1:

- Add SQLite implementation for persistence contracts.
- Save/load server identity from SQLite.
- Start writing bot/match/outbox records on key manager events.

Stage 2:

- Add replay and analytics query paths backed by SQLite.
- Add outbox worker and retry policy.

Stage 3:

- Integrate remote global registry.
- Replace mock registrar with network registrar.
- Add inbound dedupe enforcement for synced events.
