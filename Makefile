.PHONY: help build build-server build-agent build-plugins lint test test-race test-cover clean release-tag help deb deb-client dev

# Default target
help:
	@echo "Build-a-Bot Stadium - Makefile targets"
	@echo ""
	@echo "Building & Testing:"
	@echo "  make build              Build all binaries locally"
	@echo "  make build-server       Build bbs-server binary"
	@echo "  make build-agent        Build bbs-agent binary"
	@echo "  make build-plugins      Build plugin binaries"
	@echo "  make lint               Run linter and validation checks"
	@echo "  make test               Run all tests"
	@echo "  make test-race          Run tests with race detector"
	@echo "  make test-cover         Run tests with coverage report"
	@echo "  make clean              Remove build artifacts"
	@echo ""
	@echo "Development:"
	@echo "  make run-server         Run server with plugins enabled"
	@echo "  make run-server-admin   Run server in admin mode (dashboard_admin_key=password)"
	@echo "  make dev                Run lint, test, and build"
	@echo ""
	@echo "Packaging:"
	@echo "  make deb                Build .deb package (usage: make deb [VERSION=0.3.0])"
	@echo "  make deb VERSION=v0.3.0 Build .deb package with specific version"
	@echo "  make deb-client         Build bbs-client .deb (usage: make deb-client [VERSION=0.1.0])"
	@echo ""
	@echo "Release Management:"
	@echo "  make version            Show current version info"
	@echo "  make release-tag        Create annotated tag (usage: make release-tag TAG=v0.2.0)"
	@echo "  make show-changes       Show commits since last tag"
	@echo ""

# Detect version from git
VERSION ?= $(shell git describe --tags --abbrev=0 2>/dev/null || echo "v0.0.0-dev")
VERSION_CLEAN = $(shell echo $(VERSION) | sed 's/^v//')
COMMIT := $(shell git rev-parse --short HEAD 2>/dev/null || echo "unknown")
BUILD_FLAGS := -ldflags "-X main.buildVersion=$(VERSION)"

# Build targets
build: build-server build-agent build-plugins
	@echo "✓ All binaries built successfully"

build-server:
	@echo "Building bbs-server ($(VERSION))..."
	@mkdir -p /tmp/bbs-build
	cd cmd/bbs-server && go build $(BUILD_FLAGS) -o /tmp/bbs-build/bbs-server .
	@echo "✓ Built: /tmp/bbs-build/bbs-server"

build-agent:
	@echo "Building bbs-agent ($(VERSION))..."
	@mkdir -p /tmp/bbs-build
	cd cmd/bbs-agent && go build $(BUILD_FLAGS) -o /tmp/bbs-build/bbs-agent .
	@echo "✓ Built: /tmp/bbs-build/bbs-agent"

build-plugins:
	@echo "Building plugins..."
	@mkdir -p /tmp/bbs-build/plugins
	cd cmd/bbs-game-counter-plugin && go build -o /tmp/bbs-build/counter-plugin .
	cd cmd/bbs-plugin-manifest-lint && go build -o /tmp/bbs-build/bbs-plugin-manifest-lint .
	@echo "✓ Built: /tmp/bbs-build/counter-plugin"
	@echo "✓ Built: /tmp/bbs-build/bbs-plugin-manifest-lint"

# Test and validation
lint:
	@echo "Running Go vet..."
	@go vet ./...
	@echo "Validating plugin manifests..."
	@go run ./cmd/bbs-plugin-manifest-lint --dirs cmd/bbs-server/plugins/games
	@echo "✓ All checks passed"

test:
	@echo "Running tests..."
	@go test -v ./...
	@echo "✓ Tests passed"

test-race:
	@echo "Running tests with race detector..."
	@go test -race ./...
	@echo "✓ Race tests passed"

test-cover:
	@echo "Running tests with coverage..."
	@PKGS=$$(find . -name "*_test.go" -type f -print | xargs -r -n1 dirname | sort -u | sed 's#^\./#./#'); \
	if [ -z "$$PKGS" ]; then \
		echo "No test packages found"; \
		exit 0; \
	fi; \
	go test -coverprofile=coverage.out $$PKGS
	@go tool cover -func=coverage.out
	@echo "✓ Coverage report generated (coverage.out)"

clean:
	@echo "Cleaning build artifacts..."
	@rm -rf /tmp/bbs-build
	@rm -f bbs-agent bbs-server bbs-game-counter-plugin bbs-plugin-manifest-lint
	@go clean ./...
	@echo "✓ Cleaned"

# Development convenience
run-server: build-server
	@echo "Starting server with plugins enabled..."
	@BBS_ENABLE_GAME_PLUGINS=true /tmp/bbs-build/bbs-server

run-server-admin: build-server
	@echo "Starting server in admin mode (admin_key=password)..."
	@BBS_DASHBOARD_ADMIN_KEY=password BBS_ENABLE_GAME_PLUGINS=true /tmp/bbs-build/bbs-server

# Version info
version:
	@echo "Repository version: $(VERSION)"
	@echo "Latest commit: $(COMMIT)"
	@echo ""
	@echo "Recent tags:"
	@git tag -l | tail -5
	@echo ""
	@echo "Commits since last tag:"
	@git rev-list --count $(VERSION)..HEAD 2>/dev/null || echo "0"

# Release management
show-changes:
	@echo "Commits since $(VERSION):"
	@git log --oneline --decorate $(VERSION)..HEAD | head -20

release-tag:
	@if [ -z "$(TAG)" ]; then \
		echo "Error: TAG not specified. Usage: make release-tag TAG=v0.2.0"; \
		exit 1; \
	fi
	@echo "Creating release tag: $(TAG)"
	@echo ""
	@echo "Recent commits that will be included:"
	@git log --oneline -10 2>/dev/null || true
	@echo ""
	@echo "Next steps:"
	@echo "  1. Review commits above"
	@echo "  2. Update CHANGELOG.md with release notes"
	@echo "  3. Create annotated tag: git tag -a $(TAG) -m 'Release $(TAG): <description>'"
	@echo "  4. Push tag: git push origin $(TAG)"
	@echo "  5. GitHub Actions will build and publish automatically"

# Packaging
deb:
	@echo "Building Debian package..."
	@scripts/build-deb.sh $(VERSION_CLEAN)
	@echo "✓ Debian package complete"

deb-client:
	@echo "Building bbs-client Debian package ($(VERSION))..."
	@scripts/build-client-deb.sh $(VERSION)
	@echo "✓ bbs-client Debian package complete"

# Development build (for local testing)
.PHONY: dev
dev: lint test build
	@echo "✓ Development build complete"
	@echo "Binaries in /tmp/bbs-build/"
