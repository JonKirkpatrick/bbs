package games

// GameInstance defines the minimum behavior any game (Chess, Connect4)
// must provide to be hosted by our server.
type GameInstance interface {
	GetName() string
	GetState() string // A string/JSON representation for players
	ValidateMove(playerID int, move string) error
	ApplyMove(playerID int, move string) error
	IsGameOver() (bool, string) // Returns true and the winner's name
}
