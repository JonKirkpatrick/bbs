# Contributing to Build-a-Bot Stadium

Thank you for your interest in contributing! This document explains how to work on the project.

## Getting Started

### Prerequisites

- Go 1.26.1 or newer
- Git
- (Optional) Python 3.8+ for plugin testing

### Local Setup

```bash
git clone https://github.com/JonKirkpatrick/bbs.git
cd bbs

# Build everything
make build

# Run tests
make test

# Run linter and validation
make lint
```

### Convenient Development

```bash
# Start the server with plugins enabled (for manual testing)
make run-server

# Start in admin mode (dashboard_admin_key=password)
make run-server-admin

# See current version and recent commits
make version
```

## Making Changes

### Code Style

- Go: Follow [effective Go](https://golang.org/doc/effective_go)
- Python: Follow [PEP 8](https://pep8.org/)
- All code: Use meaningful variable names and add comments for non-obvious logic

### Submitting Changes

1. Create a feature branch: `git checkout -b feature/my-feature`
2. Make changes and test locally: `make test lint`
3. Commit with clear messages: `git commit -m "Add new feature: ..."`
4. Push: `git push origin feature/my-feature`
5. Open a pull request on GitHub

## Adding Plugins

Plugin authors should:

1. Reference the plugin authoring guide: [docs/guides/PLUGIN_AUTHORING.md](docs/guides/PLUGIN_AUTHORING.md)
2. Place plugins in `cmd/bbs-server/plugins/games/` for inclusion
3. Include both:
   - Plugin executable (compiled binary or executable script in any language)
   - JSON manifest with metadata and args schema
   - JavaScript viewer bundle (for rendering)
4. Test locally: `BBS_ENABLE_GAME_PLUGINS=true go run ./cmd/bbs-server`
5. Validate manifest: `go run ./cmd/bbs-plugin-manifest-lint --dirs cmd/bbs-server/plugins/games`

### Built-In Reference Plugins

- **Counter** (`cmd/bbs-game-counter-plugin/main.go`) - Simple Go plugin
- **Gridworld RL** (`cmd/bbs-server/plugins/games/gridworld_rl_plugin.py`) - Python environment
- **Guess Number** (`cmd/bbs-server/plugins/games/guess_number_plugin.py`) - Python game

## Testing

### Run All Tests

```bash
make test
# or
go test ./...
```

### Test a Single Package

```bash
go test -v ./games -run TestSomething
```

### Manual Testing

1. Start server: `make run-server`
2. Connect bot: `nc localhost 8080`
3. Send commands:
   ```
   REGISTER my_bot
   CREATE counter target=15
   JOIN my_arena
   MOVE 1
   ```

### Plugin Testing

```bash
# Run plugin directory with server
BBS_ENABLE_GAME_PLUGINS=true BBS_GAME_PLUGIN_DIR=/path/to/plugins go run ./cmd/bbs-server

# In browser, open http://localhost:3000
# Create arena to test your plugin
```

For the desktop client, verify the current flow by deploying a bot profile, selecting a live server, and confirming that the active session card's arena dropdown populates from that server before issuing JOIN.

## Documentation

- Main docs: [README.md](README.md)
- Protocol: [docs/reference/PROTOCOL.md](docs/reference/PROTOCOL.md)
- Plugin authoring: [docs/guides/PLUGIN_AUTHORING.md](docs/guides/PLUGIN_AUTHORING.md)
- Agent bridge: [docs/reference/BBS_AGENT_CONTRACT.md](docs/reference/BBS_AGENT_CONTRACT.md)
- Architecture: [docs/architecture/ARCHITECTURE.md](docs/architecture/ARCHITECTURE.md)

## Releases and Versions

This project uses semantic versioning. See [docs/releases/VERSIONING.md](docs/releases/VERSIONING.md) for:

- How versions are assigned
- Release process for maintainers
- Component versioning
- Pre-v1.0 stability guarantees

## Need Help?

- **Questions?** Open an [issue](https://github.com/JonKirkpatrick/bbs/issues)
- **Bug Report?** Include steps to reproduce and your environment
- **Feature Request?** Describe the use case and expected behavior

## Code of Conduct

Contributors are expected to:

- Be respectful and professional
- Welcome constructive feedback
- Focus on the code, not the person
- Include others and give credit

---

**Happy coding!** 🎮

For more information, see [docs/releases/VERSIONING.md](docs/releases/VERSIONING.md) and [CHANGELOG.md](CHANGELOG.md).
