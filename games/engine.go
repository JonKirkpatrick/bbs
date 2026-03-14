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
