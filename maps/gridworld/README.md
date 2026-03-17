# GridWorld Map Files

Gridworld environments are loaded from text files at arena creation time.

Create command example:

```text
CREATE gridworld map=default
```

Optional args:

- `map=<name>` map file basename (for example `default` -> `default.map`)
- `map_dir=<path>` override map directory search path
- `max_steps=<n>` override episode horizon
- `episodes=<n>` number of episodes to run before `gameover` (`0` means unbounded)

## Search Order

When loading `map=<name>`, the server searches:

1. `map_dir=...` (if provided)
2. `BBS_GRIDWORLD_MAP_DIR` env var
3. `maps/gridworld`
4. `../maps/gridworld`
5. `../../maps/gridworld`

## Format

Metadata block first, then a blank line, then integer grid values.

```text
name: default
rows: 6
cols: 8
max_steps: 64

2 0 0 0 1 0 0 3
1 1 1 0 1 0 1 0
...
```

Cell codes:

- `0` empty
- `1` wall
- `2` start
- `3` goal (win)
- `4` pit (loss)
