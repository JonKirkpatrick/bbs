package gridworld

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

const (
	cellEmpty = 0
	cellWall  = 1
	cellStart = 2
	cellGoal  = 3
	cellPit   = 4
)

type GridWorldGame struct {
	mapName       string
	rows          int
	cols          int
	grid          [][]int
	startRow      int
	startCol      int
	agentRow      int
	agentCol      int
	step          int
	maxSteps      int
	episodesTotal int
	episodeIndex  int
	episodeWins   int
	episodeLosses int
	lastTerminal  string
	done          bool
	terminal      string
}

type statePayload struct {
	Map          string   `json:"map"`
	Rows         int      `json:"rows"`
	Cols         int      `json:"cols"`
	Grid         [][]int  `json:"grid"`
	Agent        position `json:"agent"`
	Episode      int      `json:"episode"`
	Episodes     int      `json:"episodes"`
	Step         int      `json:"step"`
	MaxSteps     int      `json:"max_steps"`
	Wins         int      `json:"episode_wins"`
	Losses       int      `json:"episode_losses"`
	Turn         int      `json:"turn"`
	Done         bool     `json:"done"`
	Terminal     string   `json:"terminal,omitempty"`
	LastTerminal string   `json:"last_terminal,omitempty"`
}

type position struct {
	Row int `json:"row"`
	Col int `json:"col"`
}

type mapMeta struct {
	Name     string
	Rows     int
	Cols     int
	MaxSteps int
}

func New(args []string) (*GridWorldGame, error) {
	mapName, mapDir, maxStepsOverride, episodes := parseArgs(args)
	grid, meta, err := loadMap(mapName, mapDir)
	if err != nil {
		return nil, err
	}

	startRow, startCol, err := findStart(grid)
	if err != nil {
		return nil, err
	}

	maxSteps := meta.MaxSteps
	if maxStepsOverride > 0 {
		maxSteps = maxStepsOverride
	}
	if maxSteps <= 0 {
		maxSteps = meta.Rows * meta.Cols * 2
	}
	if episodes < 0 {
		episodes = 0
	}

	return &GridWorldGame{
		mapName:       meta.Name,
		rows:          meta.Rows,
		cols:          meta.Cols,
		grid:          grid,
		startRow:      startRow,
		startCol:      startCol,
		agentRow:      startRow,
		agentCol:      startCol,
		maxSteps:      maxSteps,
		episodesTotal: episodes,
		episodeIndex:  1,
	}, nil
}

func (g *GridWorldGame) RequiredPlayers() int {
	return 1
}

func (g *GridWorldGame) GetName() string {
	return "GridWorld"
}

func (g *GridWorldGame) EnforceMoveClock() bool {
	return false
}

func (g *GridWorldGame) SupportsHandicap() bool {
	return false
}

func (g *GridWorldGame) AdvanceEpisode() (bool, map[string]interface{}, error) {
	if !g.done {
		return false, nil, errors.New("cannot advance episode before terminal state")
	}

	if g.terminal == "win" {
		g.episodeWins++
	} else {
		g.episodeLosses++
	}

	payload := map[string]interface{}{
		"episode":        g.episodeIndex,
		"episodes":       g.episodesTotal,
		"terminal":       g.terminal,
		"episode_wins":   g.episodeWins,
		"episode_losses": g.episodeLosses,
	}

	g.lastTerminal = g.terminal

	if g.episodesTotal > 0 && g.episodeIndex >= g.episodesTotal {
		return false, payload, nil
	}

	g.episodeIndex++
	g.agentRow = g.startRow
	g.agentCol = g.startCol
	g.step = 0
	g.done = false
	g.terminal = ""

	return true, payload, nil
}

func (g *GridWorldGame) GetState() string {
	payload := statePayload{
		Map:          g.mapName,
		Rows:         g.rows,
		Cols:         g.cols,
		Grid:         g.grid,
		Agent:        position{Row: g.agentRow, Col: g.agentCol},
		Episode:      g.episodeIndex,
		Episodes:     g.episodesTotal,
		Step:         g.step,
		MaxSteps:     g.maxSteps,
		Wins:         g.episodeWins,
		Losses:       g.episodeLosses,
		Turn:         1,
		Done:         g.done,
		Terminal:     g.terminal,
		LastTerminal: g.lastTerminal,
	}
	encoded, _ := json.Marshal(payload)
	return string(encoded)
}

func (g *GridWorldGame) ValidateMove(playerID int, move string) error {
	if playerID != 1 {
		return errors.New("gridworld is a single-agent environment: player 1 only")
	}
	if g.done {
		return errors.New("episode is already complete")
	}

	nr, nc, err := g.nextPosition(move)
	if err != nil {
		return err
	}
	if nr < 0 || nr >= g.rows || nc < 0 || nc >= g.cols {
		return errors.New("move would leave the grid")
	}
	if g.grid[nr][nc] == cellWall {
		return errors.New("move blocked by wall")
	}
	return nil
}

func (g *GridWorldGame) ApplyMove(playerID int, move string) error {
	if err := g.ValidateMove(playerID, move); err != nil {
		return err
	}

	nr, nc, _ := g.nextPosition(move)
	g.agentRow = nr
	g.agentCol = nc
	g.step++

	switch g.grid[g.agentRow][g.agentCol] {
	case cellGoal:
		g.done = true
		g.terminal = "win"
	case cellPit:
		g.done = true
		g.terminal = "loss"
	default:
		if g.step >= g.maxSteps {
			g.done = true
			g.terminal = "loss"
		}
	}

	return nil
}

func (g *GridWorldGame) IsGameOver() (bool, string) {
	if !g.done {
		return false, ""
	}
	if g.terminal == "win" {
		return true, "player 1"
	}
	return true, "loss"
}

func (g *GridWorldGame) nextPosition(move string) (int, int, error) {
	action := strings.ToLower(strings.TrimSpace(move))
	nr, nc := g.agentRow, g.agentCol

	switch action {
	case "0", "up", "u", "north", "n":
		nr--
	case "1", "right", "r", "east", "e":
		nc++
	case "2", "down", "d", "south", "s":
		nr++
	case "3", "left", "l", "west", "w":
		nc--
	default:
		return 0, 0, fmt.Errorf("invalid action %q (use up/right/down/left or 0/1/2/3)", move)
	}

	return nr, nc, nil
}

func parseArgs(args []string) (mapName, mapDir string, maxSteps int, episodes int) {
	mapName = "default"
	episodes = 0
	for _, raw := range args {
		part := strings.TrimSpace(raw)
		if part == "" {
			continue
		}

		if strings.Contains(part, "=") {
			kv := strings.SplitN(part, "=", 2)
			key := strings.ToLower(strings.TrimSpace(kv[0]))
			val := strings.TrimSpace(kv[1])
			switch key {
			case "map":
				if val != "" {
					mapName = val
				}
			case "map_dir":
				mapDir = val
			case "max_steps":
				if parsed, err := strconv.Atoi(val); err == nil && parsed > 0 {
					maxSteps = parsed
				}
			case "episodes", "max_episodes":
				if parsed, err := strconv.Atoi(val); err == nil && parsed >= 0 {
					episodes = parsed
				}
			}
			continue
		}

		if mapName == "default" {
			mapName = part
		}
	}
	return mapName, mapDir, maxSteps, episodes
}

func loadMap(mapName, mapDir string) ([][]int, mapMeta, error) {
	path, err := resolveMapPath(mapName, mapDir)
	if err != nil {
		return nil, mapMeta{}, err
	}

	file, err := os.Open(path)
	if err != nil {
		return nil, mapMeta{}, fmt.Errorf("failed to open map file %q: %w", path, err)
	}
	defer file.Close()

	meta := mapMeta{Name: strings.TrimSuffix(filepath.Base(path), filepath.Ext(path))}
	values := make([]int, 0)
	inGrid := false

	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			if !inGrid {
				inGrid = true
			}
			continue
		}
		if strings.HasPrefix(line, "#") {
			continue
		}

		if !inGrid && strings.Contains(line, ":") {
			parts := strings.SplitN(line, ":", 2)
			key := strings.ToLower(strings.TrimSpace(parts[0]))
			val := strings.TrimSpace(parts[1])
			switch key {
			case "name":
				if val != "" {
					meta.Name = val
				}
			case "rows":
				meta.Rows, _ = strconv.Atoi(val)
			case "cols":
				meta.Cols, _ = strconv.Atoi(val)
			case "max_steps":
				meta.MaxSteps, _ = strconv.Atoi(val)
			}
			continue
		}

		inGrid = true
		normalized := strings.ReplaceAll(line, ",", " ")
		for _, tok := range strings.Fields(normalized) {
			v, convErr := strconv.Atoi(tok)
			if convErr != nil {
				return nil, mapMeta{}, fmt.Errorf("invalid grid integer %q in %s", tok, path)
			}
			if v < cellEmpty || v > cellPit {
				return nil, mapMeta{}, fmt.Errorf("unsupported cell value %d in %s", v, path)
			}
			values = append(values, v)
		}
	}
	if err := scanner.Err(); err != nil {
		return nil, mapMeta{}, err
	}

	if meta.Rows <= 0 || meta.Cols <= 0 {
		return nil, mapMeta{}, fmt.Errorf("map %s must define positive rows and cols in metadata", path)
	}
	if len(values) != meta.Rows*meta.Cols {
		return nil, mapMeta{}, fmt.Errorf("map %s expected %d grid integers but found %d", path, meta.Rows*meta.Cols, len(values))
	}

	grid := make([][]int, meta.Rows)
	idx := 0
	for r := 0; r < meta.Rows; r++ {
		grid[r] = make([]int, meta.Cols)
		for c := 0; c < meta.Cols; c++ {
			grid[r][c] = values[idx]
			idx++
		}
	}

	return grid, meta, nil
}

func resolveMapPath(mapName, mapDir string) (string, error) {
	name := strings.TrimSpace(mapName)
	if name == "" {
		name = "default"
	}

	candidate := name
	if !strings.HasSuffix(strings.ToLower(candidate), ".map") && !strings.Contains(candidate, "/") {
		candidate += ".map"
	}

	dirs := make([]string, 0)
	if strings.TrimSpace(mapDir) != "" {
		dirs = append(dirs, strings.TrimSpace(mapDir))
	}
	if envDir := strings.TrimSpace(os.Getenv("BBS_GRIDWORLD_MAP_DIR")); envDir != "" {
		dirs = append(dirs, envDir)
	}
	dirs = append(dirs, "maps/gridworld", "../maps/gridworld", "../../maps/gridworld")

	for _, dir := range dirs {
		path := filepath.Join(dir, candidate)
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
	}

	if _, err := os.Stat(candidate); err == nil {
		return candidate, nil
	}

	return "", fmt.Errorf("gridworld map %q not found (checked map_dir, BBS_GRIDWORLD_MAP_DIR, and maps/gridworld)", mapName)
}

func findStart(grid [][]int) (int, int, error) {
	for r := range grid {
		for c := range grid[r] {
			if grid[r][c] == cellStart {
				return r, c, nil
			}
		}
	}
	return 0, 0, errors.New("gridworld map must include at least one start cell (2)")
}
