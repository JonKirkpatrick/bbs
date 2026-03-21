# Versioning and Release Strategy

Build-a-Bot Stadium uses semantic versioning with per-component versioning for flexible release cycles.

## Versioning Scheme

### Repository Version

The primary version applies to the entire project: `vMAJOR.MINOR.PATCH`

- **MAJOR**: Breaking changes to core protocols or component APIs
- **MINOR**: New features, game plugins, or non-breaking enhancements
- **PATCH**: Bug fixes, documentation, or internal improvements

Example: `v0.1.0` → `v0.2.0` (new plugin or CLI feature) → `v0.2.1` (bug fix)

### Component Versions (Optional)

Individual components can be versioned independently:

- `bbs-agent/v0.2.0` - Local bridge for bot authors
- `bbs-game-counter-plugin/v0.1.0` - Reference Go plugin
- `bbs-plugin-manifest-lint/v0.1.0` - Manifest validation tool

Use component versions when a single component has frequent updates or needs independent release cycles. Most changes should increment the repository version instead.

## Release Process

### 1. Prepare Release

Update your branch (typically `main`) with:

```bash
# Update CHANGELOG.md with new version section
# Review all merged changes since last tag
# Update cmd/bbs-server/version.go if needed (for build-time versioning)

# Commit changelog if updated
git add CHANGELOG.md
git commit -m "Prepare v0.2.0 release"
```

### 2. Tag Release

```bash
# Repository release
git tag -a v0.2.0 -m "Release v0.2.0: new gridworld plugin, improved error handling"

# Or component release
git tag -a bbs-agent/v0.3.0 -m "bbs-agent v0.3.0: socket timeout fixes"

# Push tags to trigger CI/CD
git push origin v0.2.0
```

### 3. Automated Release Build

GitHub Actions workflow (`.github/workflows/release-bbs-server.yml`) automatically:

- Detects tag push
- Builds binaries for Linux amd64 and arm64
- Creates GitHub Release with binaries attached
- Injects version via `-ldflags -X main.buildVersion=<tag>`

### 4. Release Notes

GitHub Release notes should include:

- **Highlights**: Major features or fixes
- **Breaking Changes**: Any protocols or APIs that changed
- **Notable Fixes**: Important bug fixes
- **Contributors**: Credits for external contributors

## Before v1.0.0

Until `v1.0.0`, follow these guidelines:

- `v0.x.y` releases can have breaking changes (document in CHANGELOG)
- New protocols or major features warrant a minor version bump
- Focus on stability and API clarity to prepare for v1.0.0

## Build-Time Versioning

The server embeds version information at build time:

```bash
# Manual build with custom version
go build -ldflags "-X main.buildVersion=v0.2.0" -o /tmp/bbs-server ./cmd/bbs-server

# CI/CD provides version automatically
# Binary reports version via `/api/version` endpoint or command-line flag
```

## Version Sources (Priority Order)

When determining runtime version, the system checks:

1. `-ldflags` provided at build time
2. Git tag (if in a tagged commit)
3. Git describe fallback
4. "unreleased" (development/dirty tree)

## Backward Compatibility

**TCP Protocol (`../reference/PROTOCOL.md`)**:
- Envelope format is stable
- New command types are additive (non-breaking)
- Response envelope always includes `status`, `type`, `payload`

**Plugin RPC Contract (`games/pluginapi/protocol.go`)**:
- Current version: `1`
- Plugin protocol is versioned separately
- Backward-compatible additions only (new optional RPC methods)

**Agent Contract (`../reference/BBS_AGENT_CONTRACT.md`)**:
- Current version: `0.2`
- Pre-v1.0 changes may be breaking
- Clearly mark breaking changes in release notes

## Release Checklist

- [ ] Update CHANGELOG.md with all changes
- [ ] Verify all tests pass locally: `go test ./...`
- [ ] Run plugin manifest linter: `go run ./cmd/bbs-plugin-manifest-lint --dirs cmd/bbs-server/plugins/games`
- [ ] Ensure no uncommitted changes: `git status`
- [ ] Create annotated tag: `git tag -a v0.x.y -m "Release notes"`
- [ ] Push tag: `git push origin v0.x.y`
- [ ] Monitor GitHub Actions for successful build
- [ ] Review auto-generated Release on GitHub
- [ ] Add detailed release notes if needed

## Questions?

Refer to:
- `CHANGELOG.md` - What changed in each version
- `../reference/PROTOCOL.md` - TCP bot protocol stability guarantees
- `../reference/BBS_AGENT_CONTRACT.md` - Local bridge contract version
- `../guides/PLUGIN_AUTHORING.md` - Plugin protocol compatibility
