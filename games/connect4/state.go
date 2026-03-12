package connect4

import (
	"errors"
	"fmt"
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
	return fmt.Sprintf("Board state: %v, Turn: Player %d", c.Board, c.Turn)
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
	for row := 5; row >= 0; row-- {
		if c.Board[row][col] == 0 {
			c.Board[row][col] = playerID
			break
		}
	}

	// 4. Switch Turns
	if c.Turn == 1 {
		c.Turn = 2
	} else {
		c.Turn = 1
	}

	return nil
}

func (c *Connect4Game) IsGameOver() (bool, string) {
	// Add win-condition logic here
	return false, ""
}
