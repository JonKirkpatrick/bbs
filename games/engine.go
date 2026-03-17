package games

// GameInstance defines the minimum behavior any game (Chess, Connect4)
// must provide to be hosted by our server.
type GameInstance interface {
	GetName() string                              // The name of the game, e.g. "Connect4"
	GetState() string                             // A string/JSON representation for players
	ValidateMove(playerID int, move string) error // Validates a move without changing state (used for pre-move checks)
	ApplyMove(playerID int, move string) error    // Applies the move to the game state (after validation)
	IsGameOver() (bool, string)                   // Returns true and the winner's name
}

// EpisodicGame is an optional extension for environments that can chain
// multiple terminal episodes inside one arena lifecycle.
type EpisodicGame interface {
	// AdvanceEpisode is called after IsGameOver() reports true.
	// It returns continued=true when a new episode is ready to run.
	AdvanceEpisode() (continued bool, payload map[string]interface{}, err error)
}

// MoveClockPolicy is an optional extension for environments that do not want
// per-move timeout enforcement.
type MoveClockPolicy interface {
	EnforceMoveClock() bool
}

// HandicapPolicy is an optional extension for environments where move-time
// handicap controls are not meaningful.
type HandicapPolicy interface {
	SupportsHandicap() bool
}

// PlayerCountProvider is an optional extension for environments that can run
// with different participant counts (for example, single-agent RL worlds).
type PlayerCountProvider interface {
	RequiredPlayers() int
}

// RequiredPlayers returns how many players are required before an arena can
// activate. Games that do not implement PlayerCountProvider default to 2.
func RequiredPlayers(game GameInstance) int {
	if game == nil {
		return 2
	}
	if provider, ok := game.(PlayerCountProvider); ok {
		required := provider.RequiredPlayers()
		if required >= 1 && required <= 2 {
			return required
		}
	}
	return 2
}

// EnforceMoveClock returns whether the server should enforce per-move timeout
// checks for this game. Games default to true for backward compatibility.
func EnforceMoveClock(game GameInstance) bool {
	if game == nil {
		return true
	}
	if policy, ok := game.(MoveClockPolicy); ok {
		return policy.EnforceMoveClock()
	}
	return true
}

// SupportsHandicap returns whether handicap-based move clocks are meaningful
// for this game. Games default to true for backward compatibility.
func SupportsHandicap(game GameInstance) bool {
	if game == nil {
		return true
	}
	if policy, ok := game.(HandicapPolicy); ok {
		return policy.SupportsHandicap()
	}
	return true
}
