# Fhourstones Solver Assets

This folder contains imported Fhourstones solver sources retained for the local-bridge migration.

## Files

- `Game.c`, `TransGame.c`, `SearchGame.c` - original Fhourstones sources
- `fhourstones` - solver binary (created by build step)

## Build Solver Binary

From repository root:

```bash
cd examples/Fhourstones
gcc -O2 -std=c99 SearchGame.c -o fhourstones
```

Build is manual during transition.

## Migration Status

The legacy stdin/stdout worker adapter was removed.
The new local-bridge adapter (Unix socket / Windows named pipe) is planned next.
