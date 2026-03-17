#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$DEPLOY_DIR/../.." && pwd)"
LOG_FILE="$SCRIPT_DIR/build.log"

if ! command -v go >/dev/null 2>&1; then
  echo "[$(date -Iseconds)] build failed: go command not found" >>"$LOG_FILE"
  echo "go is required on this host to build from source." >&2
  exit 1
fi

mkdir -p "$DEPLOY_DIR/bin"

build_version=""
if command -v git >/dev/null 2>&1; then
  build_version="$(git -C "$REPO_ROOT" describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)"
fi
if [[ -z "$build_version" ]]; then
  build_version="unreleased"
fi

echo "[$(date -Iseconds)] build started version=$build_version" >>"$LOG_FILE"
(
  cd "$REPO_ROOT"
  go build -trimpath -ldflags="-s -w -X main.buildVersion=$build_version" -o "$DEPLOY_DIR/bin/bbs-server" ./cmd/bbs-server
) >>"$LOG_FILE" 2>&1

chmod +x "$DEPLOY_DIR/bin/bbs-server"
echo "[$(date -Iseconds)] build completed" >>"$LOG_FILE"
