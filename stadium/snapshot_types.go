package stadium

// SessionSnapshot is a serializable view of one active session.
type SessionSnapshot struct {
	SessionID       int      `json:"session_id"`
	BotID           string   `json:"bot_id"`
	BotName         string   `json:"bot_name"`
	HasOwnerToken   bool     `json:"has_owner_token"`
	PlayerID        int      `json:"player_id"`
	CurrentArenaID  int      `json:"current_arena_id,omitempty"`
	HasCurrentArena bool     `json:"has_current_arena"`
	Capabilities    []string `json:"capabilities"`
	Wins            int      `json:"wins"`
	Losses          int      `json:"losses"`
	Draws           int      `json:"draws"`
	IsRegistered    bool     `json:"is_registered"`
	RemoteAddr      string   `json:"remote_addr"`
}

// ObserverSnapshot is observer metadata exposed through dashboard snapshots.
type ObserverSnapshot struct {
	SessionID int    `json:"session_id"`
	BotName   string `json:"bot_name"`
}

// ArenaSnapshot is the dashboard/state representation of an arena.
type ArenaSnapshot struct {
	ID                int                `json:"id"`
	Status            string             `json:"status"`
	RequiredPlayers   int                `json:"required_players"`
	Game              string             `json:"game"`
	MoveClockEnabled  bool               `json:"move_clock_enabled"`
	HandicapSupported bool               `json:"handicap_supported"`
	AllowHandicap     bool               `json:"allow_handicap"`
	Player1Handicap   int                `json:"player1_handicap"`
	Player2Handicap   int                `json:"player2_handicap"`
	TimeLimitMS       int64              `json:"time_limit_ms"`
	Bot1TimeMS        int64              `json:"bot1_time_ms"`
	Bot2TimeMS        int64              `json:"bot2_time_ms"`
	MoveCount         int                `json:"move_count"`
	LastMove          string             `json:"last_move"`
	CreatedAt         string             `json:"created_at"`
	ActivatedAt       string             `json:"activated_at"`
	CompletedAt       string             `json:"completed_at"`
	Player1Session    int                `json:"player1_session,omitempty"`
	HasPlayer1        bool               `json:"has_player1"`
	Player1Name       string             `json:"player1_name"`
	Player2Session    int                `json:"player2_session,omitempty"`
	HasPlayer2        bool               `json:"has_player2"`
	Player2Name       string             `json:"player2_name"`
	Observers         []ObserverSnapshot `json:"observers"`
	GameState         string             `json:"game_state"`
	ObserverCount     int                `json:"observer_count"`
}

// ManagerSnapshot is the full dashboard/state view emitted by the manager.
type ManagerSnapshot struct {
	GeneratedAt     string               `json:"generated_at"`
	NextArenaID     int                  `json:"next_arena_id"`
	NextSessionID   int                  `json:"next_session_id"`
	NextMatchID     int                  `json:"next_match_id"`
	BotCount        int                  `json:"bot_count"`
	MatchCount      int                  `json:"match_count"`
	SessionCount    int                  `json:"session_count"`
	ArenaCount      int                  `json:"arena_count"`
	SubscriberCount int                  `json:"subscriber_count"`
	Sessions        []SessionSnapshot    `json:"sessions"`
	Arenas          []ArenaSnapshot      `json:"arenas"`
	Bots            []BotProfileSnapshot `json:"bots"`
	RecentMatches   []MatchRecord        `json:"recent_matches"`
}
