package connect4

import (
	"errors"
	"fmt"
	"strings"
)

type Connect4Game struct {
	Board [6][7]int
	Turn  int // 1 or 2
}

// New creates a new Connect4 game instance
func New() *Connect4Game {
	return &Connect4Game{Turn: 1}
}

func (c *Connect4Game) GetName() string {
	return "Connect4"
}

func (c *Connect4Game) GetState() string {
	s := ""
	for r := 0; r < 6; r++ {
		for col := 0; col < 7; col++ {
			s += fmt.Sprintf("%d", c.Board[r][col])
		}
		s += "\n"
	}
	s += fmt.Sprintf("TURN %d", c.Turn)
	return s
}

func (c *Connect4Game) ValidateMove(playerID int, move string) error {
	if playerID != c.Turn {
		return errors.New("not your turn")
	}

	var col int
	_, err := fmt.Sscanf(move, "%d", &col)
	if err != nil || col < 0 || col > 6 {
		return errors.New("invalid column: must be 0-6")
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

	for r := 0; r < rows; r++ {
		for col := 0; col < cols; col++ {
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
	for col := 0; col < cols; col++ {
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
