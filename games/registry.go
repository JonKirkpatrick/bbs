package games

import (
	"errors"
	"fmt"
	"strconv"

	"github.com/JonKirkpatrick/bbs/games/connect4"
	"github.com/JonKirkpatrick/bbs/games/gridworld"
)

// GameFactory now receives the remainder of the command line parts
type GameFactory func(args []string) (GameInstance, error)

func GetGame(name string, args []string) (GameInstance, error) {
	factory, exists := registry[name]
	if !exists {
		return nil, fmt.Errorf("game '%s' not found", name)
	}
	return factory(args)
}

// registry maps game names to their corresponding factory functions
// for dynamic instantiation.
var registry = map[string]GameFactory{
	"connect4": func(args []string) (GameInstance, error) {
		rows, cols := 6, 7 // Defaults
		if len(args) >= 2 {
			var err error
			rows, err = strconv.Atoi(args[0])
			if err != nil {
				return nil, errors.New("invalid rows")
			}
			cols, err = strconv.Atoi(args[1])
			if err != nil {
				return nil, errors.New("invalid columns")
			}
		}
		return connect4.New(rows, cols), nil
	},
	"gridworld": func(args []string) (GameInstance, error) {
		return gridworld.New(args)
	},
	// Future games can be added here with their own argument parsing
}
