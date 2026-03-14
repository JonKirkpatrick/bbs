package connect4

import (
	"encoding/json"
	"errors"
	"fmt"
	"strings"
)

type Connect4Game struct {
	Board [][]int
	Rows  int
	Cols  int
	Turn  int
}

type GameState struct {
	Board [][]int `json:"board"`
	Turn  int     `json:"turn"`
}

// New now accepts dimensions
func New(rows, cols int) *Connect4Game {
	board := make([][]int, rows)
	for i := range board {
		board[i] = make([]int, cols)
	}
	return &Connect4Game{
		Board: board,
		Rows:  rows,
		Cols:  cols,
		Turn:  1,
	}
}

func (c *Connect4Game) GetName() string {
	return "Connect4"
}

func (c *Connect4Game) GetState() string {
	// Convert array to slice for JSON serialization
	boardSlice := make([][]int, c.Rows)
	for i := range c.Board {
		boardSlice[i] = c.Board[i][:]
	}

	state := GameState{
		Board: boardSlice,
		Turn:  c.Turn,
	}

	bytes, _ := json.Marshal(state)
	return string(bytes)
}

func (c *Connect4Game) ValidateMove(playerID int, move string) error {
	if playerID != c.Turn {
		return errors.New("not your turn")
	}

	var col int
	_, err := fmt.Sscanf(move, "%d", &col)
	if err != nil || col < 0 || col > c.Cols-1 {
		return fmt.Errorf("invalid column: must be 0-%d", c.Cols-1)
	}

	// Check if the top row of this column is already occupied
	if c.Board[0][col] != 0 {
		return errors.New("column is full")
	}

	return nil
}

func (c *Connect4Game) ApplyMove(playerID int, move string) error {
	// 1. Validate (Internal Guard)
	if err := c.ValidateMove(playerID, move); err != nil {
		return err
	}

	// 2. Extract column (we know it's valid now)
	var col int
	fmt.Sscanf(move, "%d", &col)

	// 3. Find the lowest empty row (Gravity)
	for row := len(c.Board) - 1; row >= 0; row-- {
		if c.Board[row][col] == 0 {
			c.Board[row][col] = playerID
			break
		}
	}

	// 4. Switch Turns
	c.Turn = 3 - c.Turn // Switches between 1 and 2

	return nil
}

func (c *Connect4Game) IsGameOver() (bool, string) {
	// Check every cell for a possible winning sequence
	rows := len(c.Board)
	cols := len(c.Board[0])

	for r := range rows {
		for col := range cols {
			player := c.Board[r][col]
			if player == 0 {
				continue
			}

			// Check right (horizontal)
			if col+3 < cols &&
				player == c.Board[r][col+1] &&
				player == c.Board[r][col+2] &&
				player == c.Board[r][col+3] {
				return true, fmt.Sprintf("Player %d", player)
			}

			// Check down (vertical)
			if r+3 < rows &&
				player == c.Board[r+1][col] &&
				player == c.Board[r+2][col] &&
				player == c.Board[r+3][col] {
				return true, fmt.Sprintf("Player %d", player)
			}

			// Check diagonal down-right
			if r+3 < rows && col+3 < cols &&
				player == c.Board[r+1][col+1] &&
				player == c.Board[r+2][col+2] &&
				player == c.Board[r+3][col+3] {
				return true, fmt.Sprintf("Player %d", player)
			}

			// Check diagonal up-right
			if r-3 >= 0 && col+3 < cols &&
				player == c.Board[r-1][col+1] &&
				player == c.Board[r-2][col+2] &&
				player == c.Board[r-3][col+3] {
				return true, fmt.Sprintf("Player %d", player)
			}
		}
	}

	// Check for draw: top row is full
	full := true
	for col := range cols {
		if c.Board[0][col] == 0 {
			full = false
			break
		}
	}
	if full {
		return true, "draw"
	}

	// Game continues
	return false, ""
}

func (c *Connect4Game) String() string {
	var s strings.Builder
	s.WriteString("\n")
	for r := range c.Board {
		for col := range c.Board[r] {
			fmt.Fprintf(&s, "%d ", c.Board[r][col])
		}
		s.WriteString("\n")
	}
	return s.String()
}
