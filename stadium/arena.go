package stadium

import (
	"time"

	"github.com/JonKirkpatrick/bbs/games"
)

// Arena represents a single match instance, including players, observers, game state, and timing.
type Arena struct {
	ID                int                // Unique identifier for the arena
	Player1           *Session           // Session of Player 1 (can be nil if waiting for opponent)
	Player2           *Session           // Session of Player 2 (can be nil if waiting for opponent)
	Observers         []*Session         // List of sessions observing this arena (can be empty)
	AllowHandicap     bool               // Whether this arena allows handicap time
	MoveClockEnabled  bool               // Whether per-move timeout enforcement is enabled
	HandicapSupported bool               // Whether handicap controls are meaningful for this game
	Player1Handicap   int                // Percentage adjustment for player 1's move clock (e.g., +20 => 20% more time)
	Player2Handicap   int                // Percentage adjustment for player 2's move clock (e.g., -20 => 20% less time)
	Status            string             // "waiting", "active", "completed"
	RequiredPlayers   int                // Number of players needed before activation (1 or 2)
	Game              games.GameInstance // The game instance (rulebook) for this arena
	GameArgs          []string           // Initialization args used to construct Game (for replay fidelity)
	TimeLimit         time.Duration      // Time limit per move
	Bot1Time          time.Duration      // Remaining time for Player 1
	Bot2Time          time.Duration      // Remaining time for Player 2
	LastMove          time.Time          // Timestamp of the last move (for timeout tracking)
	CreatedAt         time.Time          // Timestamp when the arena was created
	ActivatedAt       time.Time          // Timestamp when both players were present
	CompletedAt       time.Time          // Timestamp when arena reached terminal state
	MoveHistory       []MatchMove        // Ordered move history for this arena
	WinnerPlayerID    int                // 1 or 2 when finalized with a winner; 0 otherwise
	IsDraw            bool               // True when finalized as a draw
}

// ArenaSummary is a simplified lobby view of an arena.
type ArenaSummary struct {
	ID     int    `json:"id"`
	Game   string `json:"game"`
	P1Name string `json:"p1_name"`
	P2Name string `json:"p2_name"`
	Status string `json:"status"`
}

func (a *Arena) Audience() []*Session {
	audience := make([]*Session, 0, 2+len(a.Observers))
	seen := make(map[int]struct{}, 2+len(a.Observers))

	appendUnique := func(s *Session) {
		if s == nil {
			return
		}
		if _, exists := seen[s.SessionID]; exists {
			return
		}
		seen[s.SessionID] = struct{}{}
		audience = append(audience, s)
	}

	appendUnique(a.Player1)
	appendUnique(a.Player2)
	for _, observer := range a.Observers {
		appendUnique(observer)
	}

	return audience
}

func (a *Arena) HandicapForPlayer(playerID int) int {
	switch playerID {
	case 1:
		return a.Player1Handicap
	case 2:
		return a.Player2Handicap
	default:
		return 0
	}
}

func (a *Arena) MoveLimitForPlayer(playerID int) time.Duration {
	return applyHandicapPercent(a.TimeLimit, a.HandicapForPlayer(playerID))
}

func (a *Arena) MaxMoveLimit() time.Duration {
	p1 := a.MoveLimitForPlayer(1)
	p2 := a.MoveLimitForPlayer(2)
	if p2 > p1 {
		return p2
	}
	return p1
}

func applyHandicapPercent(base time.Duration, handicapPercent int) time.Duration {
	if base <= 0 {
		return 0
	}

	multiplier := 100 + handicapPercent
	if multiplier < 10 {
		multiplier = 10
	}

	return time.Duration(int64(base) * int64(multiplier) / 100)
}
