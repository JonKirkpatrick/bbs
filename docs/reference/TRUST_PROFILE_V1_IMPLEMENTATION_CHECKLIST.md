# Trust Profile v1 Implementation Checklist

Status: Draft implementation plan
Scope: convert Trust Profile v1 into concrete code and protocol changes
Related: [TRUST_PROFILE_V1.md](TRUST_PROFILE_V1.md)

## 1. How To Use This Checklist

1. Complete phases in order.
2. Keep compatibility gates in place until strict mode is enabled.
3. Treat each checkbox as a review artifact for PRs.
4. Update protocol and contract docs in the same PR as code changes.

## 2. Current Touchpoints

Primary implementation files likely to change:

1. [cmd/bbs-server/main.go](../../cmd/bbs-server/main.go)
2. [docs/reference/PROTOCOL.md](PROTOCOL.md)
3. [docs/reference/BBS_AGENT_CONTRACT.md](BBS_AGENT_CONTRACT.md)
4. [docs/reference/TRUST_PROFILE_V1.md](TRUST_PROFILE_V1.md)

Likely adjacent code areas to identify before implementation begins:

1. server dashboard startup (`startDashboard` path)
2. agent connect/register path (`bbs-agent` command code)
3. client server-connect/deploy handshake paths in `bbs-client`
4. shared transport helpers and environment/config parsing

## 3. Phase 1: Transport Security Baseline

Goal: TLS available and enforceable for remote links.

### 3.1 Server configuration and startup

- [ ] Add transport config parsing for:
- `BBS_TLS_MODE` (`disabled|server|strict`)
- `BBS_TLS_CERT_FILE`
- `BBS_TLS_KEY_FILE`
- `BBS_TLS_CA_FILE` (optional trust chain)
- `BBS_ALLOW_PLAINTEXT_LOCALHOST`
- [ ] Validate startup config on server boot.
- [ ] Refuse invalid combinations (for example strict mode without cert/key).
- [ ] In strict mode, disable plaintext listeners.

### 3.2 Bot TCP listener

- [ ] Add TLS listener path in [cmd/bbs-server/main.go](../../cmd/bbs-server/main.go) for stadium TCP.
- [ ] Keep plaintext fallback only under explicit non-strict mode.
- [ ] Add startup log line showing active transport mode and listener type.

### 3.3 Dashboard HTTP/SSE/WS

- [ ] Add HTTPS mode for dashboard endpoints.
- [ ] Verify SSE and WS upgrade paths work under TLS.
- [ ] Ensure redirect/host handling remains consistent for dashboard URLs emitted in REGISTER payload.

### 3.4 Agent and client verification

- [ ] Add strict server certificate validation in `bbs-agent` outbound connections.
- [ ] Add strict HTTPS/WSS server certificate validation in `bbs-client` remote paths.
- [ ] Disallow insecure verification in strict mode.

### 3.5 Documentation updates

- [ ] Update [docs/reference/PROTOCOL.md](PROTOCOL.md) transport section with TLS modes and expectations.
- [ ] Update [docs/reference/BBS_AGENT_CONTRACT.md](BBS_AGENT_CONTRACT.md) with TLS transport notes for server links.

## 4. Phase 2: Ephemeral Agent Trust Tokens

Goal: authorize transient agent workloads with short-lived scoped credentials.

### 4.1 Token format and validation

- [ ] Define signed token format (JWT or compact signed envelope).
- [ ] Require claims:
- `iss`, `sub`, `aud`, `iat`, `exp`, `jti`, `scope`
- optional `source_bot_id`, `lineage`
- [ ] Add issuer key configuration for server verification.
- [ ] Add skew and TTL validation limits.

### 4.2 REGISTER flow integration

- [ ] Extend REGISTER request options to carry agent trust token (additive change).
- [ ] Validate token during REGISTER before session activation.
- [ ] Bind `aud` to local server identity.
- [ ] Return explicit auth error messages for token validation failures.

### 4.3 Scope enforcement

- [ ] Map command handlers to required scopes:
- `session.register`, `profile.update`, `arena.create`, `arena.join`, `arena.leave`, `arena.watch`, `move.submit`
- [ ] Enforce deny-by-default for missing scope.
- [ ] Re-check expiration/validity on sensitive command execution.

### 4.4 Agent contract updates

- [ ] Add token provisioning expectations to [docs/reference/BBS_AGENT_CONTRACT.md](BBS_AGENT_CONTRACT.md).
- [ ] Document transient token lifetime target and renewal expectations.

## 5. Phase 3: Replay Resistance and Proof Binding

Goal: reduce replay risk on control-plane paths.

### 5.1 Register proof hardening

- [ ] Keep nonce/timestamp fields required for protected profiles.
- [ ] Add timestamp freshness checks with configurable skew window.
- [ ] Maintain short-term `jti`/nonce replay cache.
- [ ] Fail replay attempts with deterministic auth errors.

### 5.2 Owner/admin control-plane paths

- [ ] Apply equivalent replay protections to owner/admin high-impact operations.
- [ ] Ensure one-time or short-window validity for high-privilege action proofs.

### 5.3 Audit trail

- [ ] Emit structured security audit events:
- server id, session id, issuer/sub, jti, action, result, reason, peer
- [ ] Ensure raw secrets/tokens are never logged.

## 6. Phase 4: Compatibility Gate and Strict Cutover

Goal: move from additive support to secure-by-default behavior.

### 6.1 Compatibility period

- [ ] Keep additive protocol fields while mixed clients exist.
- [ ] Add warnings when insecure modes are active.
- [ ] Add metric/log counters for plaintext and unauthenticated usage.

### 6.2 Strict profile cutover

- [ ] Set strict mode as default for remote deployment docs.
- [ ] Optionally retain plaintext only for localhost dev profile.
- [ ] Require trust token for agent REGISTER in strict mode.

### 6.3 Final documentation pass

- [ ] Update [docs/reference/PROTOCOL.md](PROTOCOL.md) with final required REGISTER fields by mode.
- [ ] Update [docs/reference/BBS_AGENT_CONTRACT.md](BBS_AGENT_CONTRACT.md) with final deployment requirements.
- [ ] Add migration notes to releases docs as needed.

## 7. Suggested PR Breakdown

PR 1: Server TLS scaffolding

- [ ] TLS config parsing and listener support
- [ ] dashboard HTTPS/WSS baseline
- [ ] docs update for transport modes

PR 2: Agent/client TLS verification

- [ ] strict verification in outbound paths
- [ ] mode-aware fallback handling

PR 3: Token model and REGISTER validation

- [ ] token parser/verifier
- [ ] REGISTER token integration
- [ ] scope checks for core commands

PR 4: Replay cache and audit hardening

- [ ] nonce/jti replay handling
- [ ] audit fields and logging policy

PR 5: Strict mode defaults and migration cleanup

- [ ] secure defaults
- [ ] compatibility mode constraints
- [ ] final docs sync

## 8. Definition of Done for Trust Profile v1

- [ ] Remote deployments can run end-to-end with TLS-only transport.
- [ ] Agent REGISTER can be authorized by short-lived signed scoped token.
- [ ] High-impact control paths include replay resistance checks.
- [ ] Audit logs capture provenance without exposing secrets.
- [ ] Protocol/agent docs accurately describe required security fields.
- [ ] Local-dev profile remains intentionally usable with explicit insecure flags.
