# Plugin Authoring Guide

This guide is for external developers building process-based game plugins for Build-a-Bot Stadium.

## Compatibility

- Plugin transport is JSONL over `stdin/stdout`.
- Plugin protocol version must match `games/pluginapi.ProtocolVersion` (currently `1`).
- Use `stderr` for logs. Keep `stdout` protocol-only.

## Plugin Runtime Model

- Server starts your plugin as a separate process.
- Server sends RPC requests such as `init`, `get_state`, and `apply_move`.
- Your plugin responds with JSON objects matching `games/pluginapi` request/response types.

Reference implementations:

- `cmd/bbs-game-counter-plugin/main.go` (Go, simple two-player game)
- `cmd/bbs-server/plugins/games/gridworld_rl_plugin.py` (Python, episodic environment with replay support)
- `games/pluginapi/server.go` (RPC server wrapper)
- `games/pluginapi/protocol.go` (protocol constants)

## Manifest Schema

A plugin manifest is a `*.json` file in the plugin directory (`plugins/games` by default).

Required top-level fields:

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `name` | string | yes | Plugin game key. Keep lowercase and stable. |
| `executable` | string | yes | Binary path or name resolvable from manifest/plugin directory. |

Optional top-level fields:

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `protocol_version` | number | no | `0` or omitted means current server default. Prefer explicit `1`. |
| `display_name` | string | no | Human-readable name in dashboard dropdown. |
| `supports_move_clock` | bool | no | Whether dashboard should expose move clock controls. |
| `supports_handicap` | bool | no | Whether dashboard should expose handicap controls. |
| `args` | array | no | Schema for dashboard game-argument form. |

`args[]` field schema:

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `key` | string | yes | Token key used to compose `key=value` launch args. |
| `label` | string | no | UI label. Falls back to `key` if omitted. |
| `input_type` | string | no | HTML input type. Supported by linter: `text`, `number`, `checkbox`, `email`, `password`, `search`, `tel`, `url`. |
| `placeholder` | string | no | UI placeholder text. |
| `default_value` | string | no | Pre-filled form value. |
| `required` | bool | no | Enforces input before form submit. |
| `help` | string | no | Inline helper text in dashboard form. |

Example manifest:

```json
{
  "protocol_version": 1,
  "name": "mygame",
  "display_name": "My Game",
  "executable": "my-game-plugin",
  "supports_move_clock": true,
  "supports_handicap": false,
  "args": [
    {
      "key": "board_size",
      "label": "Board Size",
      "input_type": "number",
      "default_value": "8",
      "required": true,
      "help": "Board edge length"
    }
  ]
}
```

## Local Workflow

1. Implement plugin game logic and start RPC server with `pluginapi.Serve(factory)`.
2. Build plugin binary.
3. Place binary and manifest in your plugin directory.
4. Start server with:

```bash
BBS_ENABLE_GAME_PLUGINS=true \
BBS_GAME_PLUGIN_DIR=/path/to/plugins \
go run ./cmd/bbs-server
```

5. Open dashboard and verify:
- Plugin appears in create-arena dropdown.
- `args` render as dynamic form fields.
- Plugin Discovery panel reports manifest as `loaded`.

## CI Validation

Repository CI includes a manifest shape check:

```bash
go run ./cmd/bbs-plugin-manifest-lint --dirs plugins/games
```

The linter validates:

- JSON shape with unknown field rejection
- `name` and `args[].key` identifier format
- duplicate plugin names
- duplicate arg keys
- allowed `input_type` values
- `protocol_version` compatibility

## Viewer Integration

Viewer rendering is now fully externalized to plugin-provided JavaScript bundles.

Required manifest fields:

- `viewer_client_entry`: path to the plugin viewer JS file.
- `supports_replay`: set `true` when the renderer supports replay timelines.

How rendering works:

- BBS sends raw frame data (`raw_state`, move metadata, terminal/winner flags).
- The viewer page loads the plugin bundle from `/viewer/plugin-entry?game=<name>`.
- The bundle registers a renderer with:
  - `window.BBSViewerPluginRuntime.register("<game_name>", { render(payload) { ... } })`

Renderer payload shape:

- `canvas`, `ctx`
- `frame` (contains `raw_state`, `move_index`, `timestamp`, `is_terminal`, `winner`)
- `frames` / `frameIndex` for replay mode
- `players`
- `mode` (`live` or `replay`)

Practical guidance:

- Keep `GetState()` deterministic and machine-parseable JSON.
- Put all visual data the renderer needs into `raw_state`.
- Return `true` from renderer `render(payload)` when the frame is handled.
- Keep bundle startup side effects minimal and register exactly once.

## Release Checklist

1. Build binary for target platform(s).
2. Validate manifest via linter command.
3. Smoke test with `BBS_ENABLE_GAME_PLUGINS=true` and your plugin directory.
4. Confirm dashboard Plugin Discovery panel shows `loaded` and no skipped reason.
5. Create arena from dashboard and from TCP `CREATE` command.
6. Verify `GetState()` output remains deterministic and machine-parseable.
7. Confirm graceful shutdown behavior on arena teardown.
8. Publish binary + manifest together.

## Troubleshooting

- Not discovered:
  - Verify `BBS_ENABLE_GAME_PLUGINS=true`.
  - Verify manifest extension is `.json`.
  - Verify `name` is present and unique.
- Manifest skipped:
  - Open dashboard Plugin Discovery panel and inspect `Reason`.
  - Run linter locally for direct path errors.
- Runtime launch failure:
  - Verify `executable` resolves correctly relative to manifest directory or absolute path.
  - Run executable directly and check stderr logs.
- Protocol hangs:
  - Ensure no non-protocol output is written to stdout.
