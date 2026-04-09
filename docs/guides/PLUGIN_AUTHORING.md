# Plugin Authoring Guide

This guide is for external developers building process-based game plugins for Build-a-Bot Stadium.

## Compatibility

- Plugin transport is JSONL over `stdin/stdout`.
- Plugin protocol version must match `games/pluginapi.ProtocolVersion` (currently `1`).
- Use `stderr` for logs. Keep `stdout` protocol-only.
- Plugins may be implemented in any language as long as the executable process speaks the JSONL RPC protocol.
- `GetState()` should return deterministic JSON text in `state` (`raw_state` in viewer payloads).
- Viewer now supports an optional frame-stream payload in `raw_state.viewer.frame_stream` for direct pixel rendering.

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
| `viewer_client_entry` | string | compatibility-required (current release) | Legacy plugin JS entry for client renderer fallback. Planned for deprecation in a future release once manifest contract is relaxed. |
| `supports_replay` | bool | no | Whether plugin renderer supports replay timelines. |

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
  "viewer_client_entry": "my_game_viewer.js",
  "supports_replay": true,
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

Viewer rendering is frame-stream first.

How viewer rendering now works (in order):

- Viewer receives live/replay frames from `/viewer/live-sse` or `/viewer/live-ws`, and replay snapshots from `/viewer/replay-data`.
- Every frame includes `raw_state` (your `GetState()` JSON string).
- If `raw_state.viewer.frame_stream` is present and valid, the viewer renders that payload directly (video-like frame playback).
- If frame stream is missing/invalid, viewer falls back to plugin JS renderer loaded from `/viewer/plugin-entry?game=<name>`.

This means plugin authors are no longer forced to rely exclusively on JavaScript rendering for every frame.

Required manifest fields:

- `viewer_client_entry`: path to plugin viewer JS file (still required by current manifest/linter contract for compatibility).

Frame-stream packet schema (inside your `GetState()` JSON):

Path: `viewer.frame_stream`

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `version` | number | no | Optional protocol marker (for example `1`). |
| `mime_type` | string | yes | Example: `image/png`, `image/jpeg`, `image/svg+xml`. |
| `encoding` | string | yes | One of: `base64`, `utf8`, `data_url`. |
| `data` | string | yes | Encoded image payload. |
| `width` | number | no | Canvas width hint. |
| `height` | number | no | Canvas height hint. |
| `frame_id` | string | no | Optional frame identifier. |
| `key_frame` | bool | no | Optional keyframe hint. |

Minimal `state` example with frame stream:

```json
{
  "state": "{\"viewer\":{\"frame_stream\":{\"version\":1,\"mime_type\":\"image/svg+xml\",\"encoding\":\"utf8\",\"data\":\"<svg ...>...</svg>\",\"width\":960,\"height\":540,\"frame_id\":\"f-42\",\"key_frame\":true}}}"
}
```

Legacy JS renderer contract (fallback path):

- Bundle is served from `/viewer/plugin-entry?game=<name>`.
- Bundle registers once via:
  - `window.BBSViewerPluginRuntime.register("<game_name>", { render(payload) { ... } })`
- This path remains supported in the current release but is expected to be deprecated in a future release as frame-stream adoption completes.

Renderer payload shape:

- `canvas`, `ctx`
- `frame` (contains `raw_state`, `move_index`, `timestamp`, `is_terminal`, `winner`)
- `frames` / `frameIndex` for replay mode
- `players`
- `mode` (`live` or `replay`)

Practical guidance:

- Keep `GetState()` deterministic and machine-parseable JSON.
- Put all visual data needed by non-JS viewers into `raw_state.viewer.frame_stream`.
- If you still ship JS fallback, return `true` from `render(payload)` when handled.
- Keep JS bundle startup side effects minimal and register exactly once.
- Prefer stable frame dimensions (`width`/`height`) to avoid canvas resize churn.

## Release Checklist

1. Build binary for target platform(s).
2. Validate manifest via linter command.
3. Smoke test with `BBS_ENABLE_GAME_PLUGINS=true` and your plugin directory.
4. Confirm dashboard Plugin Discovery panel shows `loaded` and no skipped reason.
5. Create arena from dashboard and from TCP `CREATE` command.
6. Verify `GetState()` output remains deterministic and machine-parseable.
7. If using frame stream, verify `viewer.frame_stream` has valid `mime_type`, `encoding`, and `data`.
8. Confirm live and replay viewer behavior both work with your frame payload.
9. Confirm graceful shutdown behavior on arena teardown.
10. Publish binary + manifest together.

## Troubleshooting

- Not discovered:
  - Verify `BBS_ENABLE_GAME_PLUGINS=true`.
  - Verify manifest extension is `.json`.
  - Verify `name` is present and unique.
- Manifest skipped:
  - Open dashboard Plugin Discovery panel and inspect `Reason`.
  - Run linter locally for direct path errors.
- Live/replay shows "Loading frame stream..." or blank canvas:
  - Validate `viewer.frame_stream.encoding` is one of `base64`, `utf8`, `data_url`.
  - Validate `mime_type` and `data` are non-empty on every rendered frame.
  - Verify emitted payload is valid JSON within the `state` string.
- Runtime launch failure:
  - Verify `executable` resolves correctly relative to manifest directory or absolute path.
  - Run executable directly and check stderr logs.
- Protocol hangs:
  - Ensure no non-protocol output is written to stdout.
