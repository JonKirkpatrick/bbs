# BBS Trust Profile v1

Status: Draft baseline
Scope: `bbs-server`, `bbs-agent`, `bbs-client`, dashboard streams, owner/admin/operator control paths
Audience: project maintainers and implementers
Implementation checklist: [TRUST_PROFILE_V1_IMPLEMENTATION_CHECKLIST.md](TRUST_PROFILE_V1_IMPLEMENTATION_CHECKLIST.md)

## 1. Purpose

This document defines a practical security/trust baseline for BBS that matches the intended use case:

- distributed bot composition across multiple machines/regions
- transient `bbs-agent` processes (often one-shot)
- no adversarial tournament model
- strong need for provenance, integrity, and safe remote operation

The profile favors consistency and operational simplicity over enterprise IAM complexity.

## 2. System Model

Primary network relationships:

1. `bbs-agent` to `bbs-server` (control/game session path)
2. `bbs-client` to `bbs-server` (operator UI path)
3. dashboard browser to `bbs-server` (`/`, SSE, WS)
4. optional server-to-server federation/outbox path

Trust assumptions for v1:

- Server identity must be strongly verifiable.
- Agent processes are ephemeral and not ideal as long-lived identity roots.
- Launching host/orchestrator is the stable trust anchor for agent runtime issuance.
- Application tokens (owner/admin/control) are authorization artifacts, not transport security.

## 3. Security Objectives

Mandatory:

1. Confidentiality in transit for all remote links.
2. Server authenticity for all clients/agents.
3. Bounded authorization via short-lived capabilities.
4. Replay-resistant control-plane operations.
5. Auditable provenance for high-impact actions.

Strongly recommended:

1. Minimize plaintext modes to local-dev only.
2. Keep secrets short-lived and scoped.
3. Separate transport identity from app authorization.

## 4. Threat Model (v1)

In scope:

1. Passive network eavesdropping.
2. Active MITM against remote links.
3. Credential/token replay.
4. Over-privileged or leaked long-lived secrets.
5. Cross-server confusion (agent talks to wrong server).

Out of scope for v1:

1. Host/root compromise on trusted launch machines.
2. Side-channel defenses.
3. Full PKI revocation infrastructure (OCSP/CRL at internet scale).

## 5. Trust Boundaries

1. Stable identities:
- `bbs-server` instance identity (certificate)
- optional launcher/orchestrator identity (certificate)

2. Ephemeral identities:
- agent runtime instance (short-lived credential)
- UI/browser session tokens

3. Authorization artifacts:
- owner/admin/control tokens
- signed workload capability tokens (new in profile v1)

## 6. Transport Policy

### 6.1 Global

1. Remote transport MUST use TLS.
2. Plaintext MAY be allowed only when explicitly configured for local development.
3. `InsecureSkipVerify` MUST NOT be used in production-like environments.

### 6.2 `bbs-client` -> `bbs-server`

1. HTTPS/WSS required for remote use.
2. Client verifies server certificate chain to trusted CA (public CA or BBS private CA).
3. Existing owner/admin flows remain app-layer authorization.

### 6.3 `bbs-agent` -> `bbs-server`

Recommended baseline for ephemeral agents:

1. TLS with strict server verification.
2. Agent presents short-lived workload capability token at registration.
3. Token is signed by trusted issuer (launcher/orchestrator or server enrollment endpoint).
4. Server validates signature, expiry, audience, and allowed capabilities.

Optional stronger mode:

1. mTLS at launcher/host boundary.
2. Per-workload token still required for granular action scope.

## 7. Identity and Credential Model

### 7.1 Server Identity

1. Each server has a TLS cert + key.
2. Cert SAN includes the DNS/IP clients use.
3. Rotation target: 60-90 days (or ACME-managed).

### 7.2 Agent Runtime Identity (Ephemeral)

Use short-lived signed capability token instead of long-lived per-agent cert.

Token lifetime target:

- 5 to 30 minutes default
- never longer than 2 hours in v1

Token MUST include:

1. `iss`: issuer id
2. `sub`: runtime/workload id
3. `aud`: target server id or endpoint
4. `exp` and `iat`
5. `jti` unique id
6. `scope`: allowed operations
7. `source_bot_id` (if applicable)
8. optional `lineage`/`parent_workload_id`

### 7.3 Operator/UI Identity

Keep existing owner/admin token model with hardening:

1. reduce lifetime for elevated actions
2. avoid logging raw token values
3. bind token use to selected server/session context when possible

## 8. Authorization Model

Use capability scope checks for agent operations.

Suggested v1 scopes:

1. `session.register`
2. `arena.create`
3. `arena.join`
4. `arena.leave`
5. `arena.watch`
6. `move.submit`
7. `profile.update`

Server policy:

1. Deny by default.
2. REGISTER succeeds only if token scope includes `session.register`.
3. Command handlers enforce scope per action.
4. Expired token invalidates action even if transport session is still connected.

## 9. Replay and Binding Requirements

For control-plane actions (REGISTER and sensitive owner/admin actions):

1. Require nonce + timestamp semantics.
2. Reject stale timestamp windows (recommended &lt;= 60s skew, &lt;= 120s absolute age).
3. Keep short-term nonce/jti cache to block replay.
4. Bind proof/token `aud` to target server identity.

## 10. Provenance and Audit

For high-impact events (`REGISTER`, `CREATE`, `JOIN`, `EJECT`, ownership changes):

Record structured audit fields:

1. time
2. server id
3. session id
4. source identity (`sub`/bot/session)
5. token `jti`
6. action
7. result (allow/deny + reason)
8. peer endpoint

Do not log raw secret/token values.

## 11. Configuration Profile (v1)

Suggested environment/config controls:

1. `BBS_TLS_MODE` = `disabled|server|strict`
2. `BBS_TLS_CERT_FILE`
3. `BBS_TLS_KEY_FILE`
4. `BBS_TLS_CA_FILE` (for private trust chains)
5. `BBS_ALLOW_PLAINTEXT_LOCALHOST` = `true|false`
6. `BBS_AGENT_TOKEN_ISSUER_PUBLIC_KEY`
7. `BBS_AGENT_TOKEN_MAX_SKEW_SECONDS`
8. `BBS_AGENT_TOKEN_DEFAULT_TTL_SECONDS`

Semantics:

1. `disabled`: local dev only
2. `server`: TLS available, plaintext optional by policy
3. `strict`: TLS required, plaintext refused

## 12. Rollout Plan

### Phase 0 (Now)

1. Document trust profile.
2. Keep current protocol behavior.

### Phase 1

1. Add TLS for dashboard HTTP/SSE/WS.
2. Add TLS option for bot TCP listener.
3. Add strict server verification in client/agent connectors.

### Phase 2

1. Add signed short-lived agent workload token support in REGISTER.
2. Enforce scope checks in server command handlers.
3. Add replay protection cache for REGISTER proof/token `jti`.

### Phase 3

1. Optional host-level mTLS.
2. Federation hardening with same token semantics across servers.

## 13. Compatibility Policy

1. Plaintext mode remains available only for local development during migration.
2. Remote deployment profile defaults to TLS strict mode.
3. New auth fields should be additive until strict mode is enabled.

## 14. Decision Summary

For BBS v1 trust:

1. Use TLS everywhere remote.
2. Use stable server identity.
3. Treat agent identity as short-lived workload capability, not long-lived per-process cert.
4. Keep owner/admin tokens as app-layer auth, hardened with scope/lifetime/audit.
5. Prioritize provenance and replay resistance for control plane.

This profile is intentionally concrete, implementable, and consistent with transient agent workflows and distributed metabot composition.
