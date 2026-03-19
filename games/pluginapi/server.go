package pluginapi

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
)

// Game is the minimum interface a plugin-backed game must implement.
type Game interface {
	GetName() string
	GetState() string
	ValidateMove(playerID int, move string) error
	ApplyMove(playerID int, move string) error
	IsGameOver() (bool, string)
}

// EpisodicGame is an optional extension for episodic environments.
type EpisodicGame interface {
	AdvanceEpisode() (continued bool, payload map[string]interface{}, err error)
}

// MoveClockPolicy is an optional extension for untimed environments.
type MoveClockPolicy interface {
	EnforceMoveClock() bool
}

// HandicapPolicy is an optional extension for environments without handicaps.
type HandicapPolicy interface {
	SupportsHandicap() bool
}

// PlayerCountProvider is an optional extension for 1-player environments.
type PlayerCountProvider interface {
	RequiredPlayers() int
}

// Factory constructs plugin game instances.
type Factory func(args []string) (Game, error)

// Serve runs the plugin RPC loop over stdin/stdout.
func Serve(factory Factory) error {
	if factory == nil {
		return errors.New("plugin factory is nil")
	}

	scanner := bufio.NewScanner(os.Stdin)
	writer := bufio.NewWriter(os.Stdout)
	encoder := json.NewEncoder(writer)

	var game Game
	initialized := false

	writeResponse := func(resp Response) error {
		if err := encoder.Encode(resp); err != nil {
			return err
		}
		return writer.Flush()
	}

	for scanner.Scan() {
		line := scanner.Bytes()
		var req Request
		if err := json.Unmarshal(line, &req); err != nil {
			_ = writeResponse(Response{
				ID: req.ID,
				Error: &RPCError{
					Code:    "bad_request",
					Message: "failed to decode request",
				},
			})
			continue
		}

		respondError := func(code, msg string) error {
			return writeResponse(Response{
				ID: req.ID,
				Error: &RPCError{
					Code:    code,
					Message: msg,
				},
			})
		}

		respondResult := func(value interface{}) error {
			payload, err := json.Marshal(value)
			if err != nil {
				return respondError("internal", "failed to encode result")
			}
			return writeResponse(Response{ID: req.ID, Result: payload})
		}

		requireGame := func() bool {
			if initialized && game != nil {
				return true
			}
			_ = respondError("not_initialized", "init must be called before this method")
			return false
		}

		switch req.Method {
		case MethodInit:
			var params InitParams
			if len(req.Params) > 0 {
				if err := json.Unmarshal(req.Params, &params); err != nil {
					_ = respondError("bad_request", "invalid init params")
					continue
				}
			}

			newGame, err := factory(params.Args)
			if err != nil {
				_ = respondError("init_failed", err.Error())
				continue
			}

			game = newGame
			initialized = true

			result := InitResult{
				Name:              game.GetName(),
				RequiredPlayers:   requiredPlayers(game),
				SupportsMoveClock: enforceMoveClock(game),
				SupportsHandicap:  supportsHandicap(game),
				SupportsEpisodic:  supportsEpisodic(game),
			}
			if err := respondResult(result); err != nil {
				return err
			}
		case MethodGetName:
			if !requireGame() {
				continue
			}
			if err := respondResult(NameResult{Name: game.GetName()}); err != nil {
				return err
			}
		case MethodGetState:
			if !requireGame() {
				continue
			}
			if err := respondResult(StateResult{State: game.GetState()}); err != nil {
				return err
			}
		case MethodValidateMove:
			if !requireGame() {
				continue
			}

			var params MoveParams
			if err := json.Unmarshal(req.Params, &params); err != nil {
				_ = respondError("bad_request", "invalid validate_move params")
				continue
			}

			if err := game.ValidateMove(params.PlayerID, params.Move); err != nil {
				_ = respondError("validation_failed", err.Error())
				continue
			}

			if err := respondResult(Empty{}); err != nil {
				return err
			}
		case MethodApplyMove:
			if !requireGame() {
				continue
			}

			var params MoveParams
			if err := json.Unmarshal(req.Params, &params); err != nil {
				_ = respondError("bad_request", "invalid apply_move params")
				continue
			}

			if err := game.ApplyMove(params.PlayerID, params.Move); err != nil {
				_ = respondError("apply_failed", err.Error())
				continue
			}

			if err := respondResult(Empty{}); err != nil {
				return err
			}
		case MethodIsGameOver:
			if !requireGame() {
				continue
			}

			over, winner := game.IsGameOver()
			if err := respondResult(IsGameOverResult{IsGameOver: over, Winner: winner}); err != nil {
				return err
			}
		case MethodAdvanceEpisode:
			if !requireGame() {
				continue
			}

			episodic, ok := game.(EpisodicGame)
			if !ok {
				_ = respondError("unsupported", "game does not support advance_episode")
				continue
			}

			continued, payload, err := episodic.AdvanceEpisode()
			if err != nil {
				_ = respondError("advance_failed", err.Error())
				continue
			}

			if err := respondResult(AdvanceEpisodeResult{Continued: continued, Payload: payload}); err != nil {
				return err
			}
		case MethodShutdown:
			if err := respondResult(Empty{}); err != nil {
				return err
			}
			return nil
		default:
			_ = respondError("unknown_method", fmt.Sprintf("unsupported method %q", req.Method))
		}
	}

	if err := scanner.Err(); err != nil && !errors.Is(err, io.EOF) {
		return err
	}

	return nil
}

func requiredPlayers(game Game) int {
	if game == nil {
		return 2
	}
	if provider, ok := game.(PlayerCountProvider); ok {
		value := provider.RequiredPlayers()
		if value >= 0 && value <= 2 {
			return value
		}
	}
	return 2
}

func enforceMoveClock(game Game) bool {
	if game == nil {
		return true
	}
	if policy, ok := game.(MoveClockPolicy); ok {
		return policy.EnforceMoveClock()
	}
	return true
}

func supportsHandicap(game Game) bool {
	if game == nil {
		return true
	}
	if policy, ok := game.(HandicapPolicy); ok {
		return policy.SupportsHandicap()
	}
	return true
}

func supportsEpisodic(game Game) bool {
	if game == nil {
		return false
	}
	_, ok := game.(EpisodicGame)
	return ok
}
