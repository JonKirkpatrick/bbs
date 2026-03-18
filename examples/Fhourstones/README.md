# Fhourstones Solver Assets

This folder contains imported Fhourstones solver sources kept for integration experiments.

## Files

- `Game.c`, `TransGame.c`, `SearchGame.c` - upstream solver sources
- `fhourstones` - local solver binary (manual build)

## Build

From repository root:

```bash
cd examples/Fhourstones
gcc -O2 -std=c99 SearchGame.c -o fhourstones
```

## Current Status

Legacy stdin/stdout worker adapter was removed.

Current plugin direction is process-based game plugins (`games/pluginapi` + manifest-driven loading). If Fhourstones is reintroduced as a first-class runtime module, it should be wrapped as a process plugin or a bridge-compatible policy worker.
