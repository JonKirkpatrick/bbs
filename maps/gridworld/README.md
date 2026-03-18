# GridWorld Map Files

Gridworld is a built-in environment that runs inside the same arena/runtime model as competitive games.

Maps are loaded from text files at arena creation time.

Example create:

```text
CREATE gridworld map=default
```

Supported args:

- `map=<name>` map basename (`default` -> `default.map`)
- `map_dir=<path>` map directory override
- `max_steps=<n>` episode horizon override
- `episodes=<n>` number of episodes before `gameover` (`0` means unbounded)

## Search Order

Map resolution order:

1. `map_dir=...` in create args
2. `BBS_GRIDWORLD_MAP_DIR`
3. `maps/gridworld`
4. `../maps/gridworld`
5. `../../maps/gridworld`

## File Format

Metadata header, blank line, then integer grid rows.

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
