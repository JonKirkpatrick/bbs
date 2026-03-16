package stadium

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"errors"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
)

// Manager is the central coordinator for all arenas and sessions in the Build-a-Bot Stadium.
// It handles arena creation, player matchmaking, session management, and periodic cleanup of inactive arenas.
type Manager struct {
	mu             sync.Mutex
	Arenas         map[int]*Arena
	ActiveSessions map[int]*Session
	BotProfiles    map[string]*BotProfile
	MatchHistory   []MatchRecord
	subscribers    map[chan StadiumEvent]struct{} // For dashboard updates
	nextArenaID    int
	nextSessionID  int
	nextMatchID    int
}

type StadiumEvent struct {
	Type    string      `json:"type"`
	Payload interface{} `json:"payload"`
}

type RegistrationResult struct {
	SessionID      int    `json:"session_id"`
	BotID          string `json:"bot_id"`
	BotSecret      string `json:"bot_secret,omitempty"`
	IsNewIdentity  bool   `json:"is_new_identity"`
	Name           string `json:"name"`
	GamesPlayed    int    `json:"games_played"`
	Wins           int    `json:"wins"`
	Losses         int    `json:"losses"`
	Draws          int    `json:"draws"`
	RegisteredAt   string `json:"registered_at"`
	Authentication string `json:"authentication"`
}

type BotProfile struct {
	BotID             string
	BotSecret         string
	DisplayName       string
	CreatedAt         time.Time
	LastSeenAt        time.Time
	RegistrationCount int
	GamesPlayed       int
	Wins              int
	Losses            int
	Draws             int
}

type BotProfileSnapshot struct {
	BotID             string `json:"bot_id"`
	DisplayName       string `json:"display_name"`
	CreatedAt         string `json:"created_at"`
	LastSeenAt        string `json:"last_seen_at"`
	RegistrationCount int    `json:"registration_count"`
	GamesPlayed       int    `json:"games_played"`
	Wins              int    `json:"wins"`
	Losses            int    `json:"losses"`
	Draws             int    `json:"draws"`
}

type MatchMove struct {
	Number     int    `json:"number"`
	PlayerID   int    `json:"player_id"`
	SessionID  int    `json:"session_id"`
	BotID      string `json:"bot_id"`
	BotName    string `json:"bot_name"`
	Move       string `json:"move"`
	ElapsedMS  int64  `json:"elapsed_ms"`
	OccurredAt string `json:"occurred_at"`
}

type MatchParticipant struct {
	SessionID    int      `json:"session_id"`
	BotID        string   `json:"bot_id"`
	BotName      string   `json:"bot_name"`
	Capabilities []string `json:"capabilities"`
	RemoteAddr   string   `json:"remote_addr"`
}

type MatchRecord struct {
	MatchID        int                `json:"match_id"`
	ArenaID        int                `json:"arena_id"`
	Game           string             `json:"game"`
	TerminalStatus string             `json:"terminal_status"`
	EndReason      string             `json:"end_reason"`
	WinnerPlayerID int                `json:"winner_player_id"`
	WinnerBotID    string             `json:"winner_bot_id"`
	WinnerBotName  string             `json:"winner_bot_name"`
	IsDraw         bool               `json:"is_draw"`
	StartedAt      string             `json:"started_at"`
	EndedAt        string             `json:"ended_at"`
	Player1        MatchParticipant   `json:"player1"`
	Player2        MatchParticipant   `json:"player2"`
	Observers      []ObserverSnapshot `json:"observers"`
	MoveCount      int                `json:"move_count"`
	MoveSequence   []string           `json:"move_sequence"`
	CompactMoves   string             `json:"compact_moves"`
	Moves          []MatchMove        `json:"moves"`
	FinalGameState string             `json:"final_game_state"`
}

// Arena represents a single match instance, including the two players, any observers, the game state, and timing information.
type Arena struct {
	ID            int                // Unique identifier for the arena
	Player1       *Session           // Session of Player 1 (can be nil if waiting for opponent)
	Player2       *Session           // Session of Player 2 (can be nil if waiting for opponent)
	Observers     []*Session         // List of sessions observing this arena (can be empty)
	AllowHandicap bool               // Whether this arena allows handicap time
	Status        string             // "waiting", "active", "completed"
	Game          games.GameInstance // The game instance (rulebook) for this arena
	TimeLimit     time.Duration      // Time limit per move
	Bot1Time      time.Duration      // Remaining time for Player 1
	Bot2Time      time.Duration      // Remaining time for Player 2
	LastMove      time.Time          // Timestamp of the last move (for timeout tracking)
	CreatedAt     time.Time          // Timestamp when the arena was created
	ActivatedAt   time.Time          // Timestamp when both players were present
	CompletedAt   time.Time          // Timestamp when arena reached terminal state
	MoveHistory   []MatchMove        // Ordered move history for this arena
}

// ArenaSummary is a simplified struct used for listing arenas without exposing full game state or player details.
type ArenaSummary struct {
	ID     int    `json:"id"`
	Game   string `json:"game"`
	P1Name string `json:"p1_name"`
	P2Name string `json:"p2_name"`
	Status string `json:"status"`
}

type SessionSnapshot struct {
	SessionID       int      `json:"session_id"`
	BotID           string   `json:"bot_id"`
	BotName         string   `json:"bot_name"`
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

type ObserverSnapshot struct {
	SessionID int    `json:"session_id"`
	BotName   string `json:"bot_name"`
}

type ArenaSnapshot struct {
	ID             int                `json:"id"`
	Status         string             `json:"status"`
	Game           string             `json:"game"`
	AllowHandicap  bool               `json:"allow_handicap"`
	TimeLimitMS    int64              `json:"time_limit_ms"`
	Bot1TimeMS     int64              `json:"bot1_time_ms"`
	Bot2TimeMS     int64              `json:"bot2_time_ms"`
	MoveCount      int                `json:"move_count"`
	LastMove       string             `json:"last_move"`
	CreatedAt      string             `json:"created_at"`
	ActivatedAt    string             `json:"activated_at"`
	CompletedAt    string             `json:"completed_at"`
	Player1Session int                `json:"player1_session,omitempty"`
	HasPlayer1     bool               `json:"has_player1"`
	Player1Name    string             `json:"player1_name"`
	Player2Session int                `json:"player2_session,omitempty"`
	HasPlayer2     bool               `json:"has_player2"`
	Player2Name    string             `json:"player2_name"`
	Observers      []ObserverSnapshot `json:"observers"`
	GameState      string             `json:"game_state"`
	ObserverCount  int                `json:"observer_count"`
}

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

// DefaultManager is the global instance of the Manager that handles all arenas and sessions in the stadium.
var DefaultManager = &Manager{}

// init initializes the DefaultManager and starts the watchdog goroutine for arena cleanup.
func init() {
	DefaultManager = &Manager{
		Arenas:         make(map[int]*Arena),
		nextArenaID:    1,
		ActiveSessions: make(map[int]*Session),
		BotProfiles:    make(map[string]*BotProfile),
		MatchHistory:   make([]MatchRecord, 0),
		subscribers:    make(map[chan StadiumEvent]struct{}),
		nextSessionID:  1,
		nextMatchID:    1,
	}
	DefaultManager.StartWatchdog()
}

// StartWatchdog launches a background goroutine that periodically checks all arenas for timeouts and cleans up completed matches.
func (m *Manager) StartWatchdog() {
	ticker := time.NewTicker(10 * time.Second)
	go func() {
		for range ticker.C {
			m.mu.Lock()
			for id, arena := range m.Arenas {

				switch arena.Status {
				case "active":
					// Active games are strictly timed
					if time.Since(arena.LastMove) > (arena.TimeLimit * 3) {
						m.terminateArena(id, "Arena closed: Active game timed out.")
					}
				case "completed":
					// Completed games can linger briefly for stats/spectators
					if time.Since(arena.LastMove) > (1 * time.Minute) {
						m.terminateArena(id, "Arena closed: Match concluded.")
					}
				case "waiting":
					// Waiting arenas can live for an hour
					if time.Since(arena.LastMove) > (1 * time.Hour) {
						m.terminateArena(id, "Arena closed: Lobby timed out.")
					}
				case "aborted":
					// Aborted arenas can live for a short time for debugging
					if time.Since(arena.LastMove) > (5 * time.Minute) {
						m.terminateArena(id, "Arena closed: Match aborted.")
					}
				}
			}
			m.mu.Unlock()
		}
	}()
}

// terminateArena is a helper method to cleanly close an arena and notify participants of the reason.
func (m *Manager) terminateArena(id int, reason string) {
	if arena, ok := m.Arenas[id]; ok {
		arena.NotifyAll("error", reason)
		delete(m.Arenas, id)
		m.broadcastArenaListLocked()
	}
}

// DestroyArena is a public method to forcefully remove an arena, typically called when a player leaves or a match ends.
func (m *Manager) DestroyArena(id int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.Arenas, id)
	m.broadcastArenaListLocked()
}

// NotifyOpponent sends a message to the opponent of the given actorID (1 or 2) in the arena.
func (a *Arena) NotifyOpponent(actorID int, message string) {
	var opponent *Session
	if actorID == 1 {
		opponent = a.Player2
	} else {
		opponent = a.Player1
	}

	if opponent != nil && opponent.Conn != nil {
		fmt.Fprintf(opponent.Conn, "UPDATE: %s\n", message)
	}
}

// NotifyAll sends a message to both players and all observers in the arena.
func (a *Arena) NotifyAll(msgType string, payload interface{}) {
	res := Response{
		Status:  "ok",
		Type:    msgType,
		Payload: payload,
	}

	// Notify Players
	if a.Player1 != nil {
		a.Player1.SendJSON(res)
	}
	if a.Player2 != nil {
		a.Player2.SendJSON(res)
	}

	// Notify Observers
	for _, obs := range a.Observers {
		if obs != nil {
			obs.SendJSON(res)
		}
	}
}

// ListMatches returns a summary of all current arenas, including their ID, game type, player names, and status, for display in the lobby.
func (m *Manager) ListMatches() []ArenaSummary {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.listMatches()
}

// listMatches is an internal method that compiles a list of ArenaSummary structs for all current arenas, used by ListMatches and when broadcasting new arena creation to dashboards.
func (m *Manager) listMatches() []ArenaSummary {
	var list []ArenaSummary
	for id, arena := range m.Arenas {
		summary := ArenaSummary{
			ID:     id,
			Game:   arena.Game.GetName(),
			Status: arena.Status,
		}
		if arena.Player1 != nil {
			summary.P1Name = arena.Player1.BotName
		}
		if arena.Player2 != nil {
			summary.P2Name = arena.Player2.BotName
		}
		list = append(list, summary)
	}
	return list
}

// AddObserver allows a session to start observing an arena, receiving updates without participating as a player.
func (m *Manager) AddObserver(arenaID int, observer *Session) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	arena, exists := m.Arenas[arenaID]
	if !exists {
		return errors.New("arena not found")
	}

	arena.Observers = append(arena.Observers, observer)
	observer.CurrentArena = arena
	m.broadcastArenaListLocked()
	return nil
}

// CreateArena now accepts the fully-initialized GameInstance.
func (m *Manager) CreateArena(game games.GameInstance, timeLimit time.Duration, allowHandicap bool) int {
	m.mu.Lock()
	defer m.mu.Unlock()

	id := m.nextArenaID
	m.nextArenaID++

	m.Arenas[id] = &Arena{
		ID:            id,
		Game:          game,
		TimeLimit:     timeLimit,
		AllowHandicap: allowHandicap,
		Status:        "waiting",
		Observers:     make([]*Session, 0),
		MoveHistory:   make([]MatchMove, 0),
		CreatedAt:     time.Now(),
		LastMove:      time.Now(),
	}
	m.broadcastArenaListLocked()
	return id
}

// JoinArena allows a session to join an existing arena as a player, assigning them to Player 1 or Player 2 as appropriate, and starts the game if both players are present.
func (m *Manager) JoinArena(arenaID int, s *Session, handicap int) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	arena, exists := m.Arenas[arenaID]
	if !exists {
		return errors.New("arena not found")
	}

	if arena.Player1 == nil {
		arena.Player1 = s
		s.PlayerID = 1
	} else if arena.Player2 == nil {
		arena.Player2 = s
		s.PlayerID = 2
		// Status update happens inside activateArena now
	} else {
		return errors.New("arena full")
	}

	s.CurrentArena = arena

	// 1. Prepare the Payload for the Bot
	// This gives the bot its constraints immediately upon joining.
	manifest := map[string]interface{}{
		"arena_id":      arena.ID,
		"player_id":     s.PlayerID,
		"game":          arena.Game.GetName(),
		"time_limit_ms": arena.TimeLimit.Milliseconds(),
	}

	// 2. Send the confirmation to the bot
	s.SendJSON(Response{Status: "ok", Type: "join", Payload: manifest})

	// 3. If the arena is now ready, kick off the game
	if arena.Player1 != nil && arena.Player2 != nil {
		m.activateArena(arena)
	}

	m.broadcastArenaListLocked()
	return nil
}

// HandlePlayerLeave is called when a session disconnects or quits, ensuring that the arena is properly cleaned up and the opponent is notified.
func (m *Manager) HandlePlayerLeave(s *Session) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if s.CurrentArena != nil {
		arena := s.CurrentArena

		// If the leaving session is an observer, remove them from the slice and
		// return early — the match itself is unaffected.
		if arena.Player1 != s && arena.Player2 != s {
			for i, obs := range arena.Observers {
				if obs == s {
					arena.Observers = append(arena.Observers[:i], arena.Observers[i+1:]...)
					break
				}
			}
			s.CurrentArena = nil
			m.broadcastArenaListLocked()
			return
		}

		winnerPlayerID := 0

		if arena.Player1 == s && arena.Player2 != nil {
			winnerPlayerID = 2
		}
		if arena.Player2 == s && arena.Player1 != nil {
			winnerPlayerID = 1
		}

		status := "aborted"
		if winnerPlayerID != 0 {
			status = "completed"
		}

		arena.NotifyAll("error", "Player "+s.BotName+" left.")
		_, _ = m.finalizeArenaLocked(arena, "player_left", status, winnerPlayerID, false)
		m.broadcastArenaListLocked()
	}
}

// activateArena sets the arena status to active,
// initializes player time based on handicap settings,
// and notifies both players that the game has started.
func (m *Manager) activateArena(a *Arena) {
	a.Status = "active"
	a.LastMove = time.Now()
	a.ActivatedAt = time.Now()

	// Initialize Bot Time (Apply handicap if applicable)
	a.Bot1Time = a.TimeLimit
	if a.AllowHandicap {
		// Example: Apply handicap as a percentage of total time
		a.Bot2Time = a.TimeLimit + (a.TimeLimit / 10)
	} else {
		a.Bot2Time = a.TimeLimit
	}

	// Notify both bots that the game is ON
	msg := "Game Start! Opponent: " + a.Player1.BotName + " vs " + a.Player2.BotName
	a.Player1.SendJSON(Response{"ok", "info", msg})
	a.Player2.SendJSON(Response{"ok", "info", msg})
}

// RegisterSession now captures the full profile of the bot upon entry.
func (m *Manager) RegisterSession(s *Session, name, requestedBotID, providedSecret string, caps []string) (RegistrationResult, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var result RegistrationResult

	now := time.Now()
	requestedBotID = normalizeIdentityInput(requestedBotID)
	providedSecret = normalizeIdentityInput(providedSecret)

	if name == "" {
		return result, errors.New("name is required")
	}

	var profile *BotProfile
	newIdentity := false

	if requestedBotID == "" {
		botID, err := newToken("bot", 12)
		if err != nil {
			return result, errors.New("failed to generate bot id")
		}
		secret, err := newToken("sec", 24)
		if err != nil {
			return result, errors.New("failed to generate bot secret")
		}

		for {
			if _, exists := m.BotProfiles[botID]; !exists {
				break
			}
			botID, err = newToken("bot", 12)
			if err != nil {
				return result, errors.New("failed to generate unique bot id")
			}
		}

		profile = &BotProfile{
			BotID:       botID,
			BotSecret:   secret,
			DisplayName: name,
			CreatedAt:   now,
			LastSeenAt:  now,
		}
		m.BotProfiles[botID] = profile
		newIdentity = true
	} else {
		existing, exists := m.BotProfiles[requestedBotID]
		if !exists {
			return result, errors.New("unknown bot_id; send \"\" for bot_id and bot_secret to request a new identity")
		}
		if providedSecret == "" {
			return result, errors.New("bot_secret required for existing bot_id")
		}
		if subtle.ConstantTimeCompare([]byte(existing.BotSecret), []byte(providedSecret)) != 1 {
			return result, errors.New("invalid bot_secret")
		}

		profile = existing
		profile.DisplayName = name
		profile.LastSeenAt = now
	}

	// Avoid multiple simultaneous sessions for the same persistent identity.
	for _, sess := range m.ActiveSessions {
		if sess.BotID == profile.BotID {
			return result, errors.New("bot already connected")
		}
	}

	// 2. Set ID and core flags
	s.SessionID = m.nextSessionID
	m.nextSessionID++
	s.IsRegistered = true
	s.BotID = profile.BotID
	s.Capabilities = caps // Store what this bot can actually play
	s.PlayerID = 0
	s.CurrentArena = nil

	// 3. Identity assignment
	s.BotName = name
	s.Wins = profile.Wins
	s.Losses = profile.Losses
	s.Draws = profile.Draws

	profile.RegistrationCount++

	m.ActiveSessions[s.SessionID] = s
	m.broadcastArenaListLocked()

	result = RegistrationResult{
		SessionID:      s.SessionID,
		BotID:          profile.BotID,
		IsNewIdentity:  newIdentity,
		Name:           s.BotName,
		GamesPlayed:    profile.GamesPlayed,
		Wins:           profile.Wins,
		Losses:         profile.Losses,
		Draws:          profile.Draws,
		RegisteredAt:   now.UTC().Format(time.RFC3339Nano),
		Authentication: "id+secret",
	}

	if newIdentity {
		result.BotSecret = profile.BotSecret
	}

	return result, nil
}

// UnregisterSession removes a session from the manager's active sessions, typically called when a bot disconnects or quits.
func (m *Manager) UnregisterSession(sessionID int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.ActiveSessions, sessionID)
	m.broadcastArenaListLocked()
}

func (m *Manager) EjectSession(sessionID int, reason string) error {
	m.mu.Lock()
	sess, exists := m.ActiveSessions[sessionID]
	if !exists {
		m.mu.Unlock()
		return errors.New("session not found")
	}

	if reason == "" {
		reason = "Removed by dashboard admin"
	}

	if sess.CurrentArena != nil {
		arena := sess.CurrentArena
		arena.NotifyAll("error", "Player "+sess.BotName+" was ejected: "+reason)
		winnerPlayerID := 0
		status := "aborted"
		if arena.Player1 == sess && arena.Player2 != nil {
			winnerPlayerID = 2
			status = "completed"
		}
		if arena.Player2 == sess && arena.Player1 != nil {
			winnerPlayerID = 1
			status = "completed"
		}
		_, _ = m.finalizeArenaLocked(arena, "admin_eject", status, winnerPlayerID, false)
	}

	delete(m.ActiveSessions, sessionID)
	m.broadcastArenaListLocked()
	m.mu.Unlock()

	if sess.Conn != nil {
		sess.SendJSON(Response{Status: "err", Type: "ejected", Payload: reason})
		sess.Conn.Close()
	}

	return nil
}

// UpdateSessionProfile allows a session to update its profile information, such as name or capabilities, while ensuring thread safety.
func (m *Manager) UpdateSessionProfile(sess *Session, key, val string) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	switch key {
	case "name":
		sess.BotName = val
	case "capability":
		sess.Capabilities = append(sess.Capabilities, val)
	default:
		return errors.New("unknown field")
	}

	if profile, ok := m.BotProfiles[sess.BotID]; ok {
		profile.DisplayName = sess.BotName
		profile.LastSeenAt = time.Now()
	}

	m.broadcastArenaListLocked()
	return nil
}

func (m *Manager) RecordMove(arenaID int, actor *Session, move string, elapsed time.Duration) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	arena, exists := m.Arenas[arenaID]
	if !exists {
		return errors.New("arena not found")
	}

	rec := MatchMove{
		Number:     len(arena.MoveHistory) + 1,
		PlayerID:   actor.PlayerID,
		SessionID:  actor.SessionID,
		BotID:      actor.BotID,
		BotName:    actor.BotName,
		Move:       move,
		ElapsedMS:  elapsed.Milliseconds(),
		OccurredAt: time.Now().UTC().Format(time.RFC3339Nano),
	}

	arena.MoveHistory = append(arena.MoveHistory, rec)
	arena.LastMove = time.Now()
	m.broadcastArenaListLocked()
	return nil
}

func (m *Manager) FinalizeArena(arenaID int, endReason string, winnerPlayerID int, isDraw bool) (MatchRecord, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	arena, exists := m.Arenas[arenaID]
	if !exists {
		return MatchRecord{}, errors.New("arena not found")
	}

	record, err := m.finalizeArenaLocked(arena, endReason, "completed", winnerPlayerID, isDraw)
	if err != nil {
		return MatchRecord{}, err
	}

	m.broadcastArenaListLocked()
	return record, nil
}

func (m *Manager) finalizeArenaLocked(arena *Arena, endReason, terminalStatus string, winnerPlayerID int, isDraw bool) (MatchRecord, error) {
	if arena == nil {
		return MatchRecord{}, errors.New("arena is nil")
	}

	if arena.CompletedAt.IsZero() == false {
		return MatchRecord{}, errors.New("arena already finalized")
	}

	now := time.Now()
	arena.Status = terminalStatus
	arena.CompletedAt = now
	arena.LastMove = now

	record := MatchRecord{
		MatchID:        m.nextMatchID,
		ArenaID:        arena.ID,
		TerminalStatus: terminalStatus,
		EndReason:      endReason,
		WinnerPlayerID: winnerPlayerID,
		IsDraw:         isDraw,
		StartedAt:      arena.CreatedAt.UTC().Format(time.RFC3339Nano),
		EndedAt:        now.UTC().Format(time.RFC3339Nano),
		Observers:      make([]ObserverSnapshot, 0, len(arena.Observers)),
		Moves:          append([]MatchMove(nil), arena.MoveHistory...),
	}
	m.nextMatchID++

	if arena.ActivatedAt.IsZero() {
		record.StartedAt = arena.CreatedAt.UTC().Format(time.RFC3339Nano)
	} else {
		record.StartedAt = arena.ActivatedAt.UTC().Format(time.RFC3339Nano)
	}

	if arena.Game != nil {
		record.Game = arena.Game.GetName()
		record.FinalGameState = arena.Game.GetState()
	}

	record.Player1 = participantFromSession(arena.Player1)
	record.Player2 = participantFromSession(arena.Player2)

	for _, observer := range arena.Observers {
		if observer == nil {
			continue
		}
		record.Observers = append(record.Observers, ObserverSnapshot{
			SessionID: observer.SessionID,
			BotName:   observer.BotName,
		})
	}

	record.MoveSequence = make([]string, 0, len(arena.MoveHistory))
	for _, move := range arena.MoveHistory {
		record.MoveSequence = append(record.MoveSequence, move.Move)
	}
	record.MoveCount = len(record.MoveSequence)
	record.CompactMoves = strings.Join(record.MoveSequence, ",")

	winnerProfileID := ""
	winnerName := ""
	if winnerPlayerID == 1 && arena.Player1 != nil {
		winnerProfileID = arena.Player1.BotID
		winnerName = arena.Player1.BotName
	}
	if winnerPlayerID == 2 && arena.Player2 != nil {
		winnerProfileID = arena.Player2.BotID
		winnerName = arena.Player2.BotName
	}
	record.WinnerBotID = winnerProfileID
	record.WinnerBotName = winnerName

	m.applyOutcomeToProfilesLocked(arena, winnerPlayerID, isDraw)

	m.MatchHistory = append(m.MatchHistory, record)

	if arena.Player1 != nil {
		arena.Player1.CurrentArena = nil
		arena.Player1.PlayerID = 0
	}
	if arena.Player2 != nil {
		arena.Player2.CurrentArena = nil
		arena.Player2.PlayerID = 0
	}
	for _, observer := range arena.Observers {
		if observer != nil {
			observer.CurrentArena = nil
		}
	}

	// Null out arena's references to sessions so the watchdog's deferred
	// terminateArena call cannot re-notify participants after we already have.
	arena.Player1 = nil
	arena.Player2 = nil
	arena.Observers = nil

	return record, nil
}

func (m *Manager) applyOutcomeToProfilesLocked(arena *Arena, winnerPlayerID int, isDraw bool) {
	profiles := make([]*BotProfile, 0, 2)
	if arena.Player1 != nil {
		if p, ok := m.BotProfiles[arena.Player1.BotID]; ok {
			profiles = append(profiles, p)
		}
	}
	if arena.Player2 != nil {
		if p, ok := m.BotProfiles[arena.Player2.BotID]; ok {
			profiles = append(profiles, p)
		}
	}

	for _, profile := range profiles {
		profile.GamesPlayed++
		profile.LastSeenAt = time.Now()
	}

	if isDraw {
		for _, profile := range profiles {
			profile.Draws++
		}
	} else {
		if winnerPlayerID == 1 && arena.Player1 != nil {
			if p, ok := m.BotProfiles[arena.Player1.BotID]; ok {
				p.Wins++
			}
			if arena.Player2 != nil {
				if p, ok := m.BotProfiles[arena.Player2.BotID]; ok {
					p.Losses++
				}
			}
		}
		if winnerPlayerID == 2 && arena.Player2 != nil {
			if p, ok := m.BotProfiles[arena.Player2.BotID]; ok {
				p.Wins++
			}
			if arena.Player1 != nil {
				if p, ok := m.BotProfiles[arena.Player1.BotID]; ok {
					p.Losses++
				}
			}
		}
	}

	for _, sess := range m.ActiveSessions {
		if profile, ok := m.BotProfiles[sess.BotID]; ok {
			sess.Wins = profile.Wins
			sess.Losses = profile.Losses
			sess.Draws = profile.Draws
		}
	}
}

func participantFromSession(s *Session) MatchParticipant {
	participant := MatchParticipant{}
	if s == nil {
		return participant
	}

	participant.SessionID = s.SessionID
	participant.BotID = s.BotID
	participant.BotName = s.BotName
	participant.Capabilities = append([]string(nil), s.Capabilities...)
	if s.Conn != nil && s.Conn.RemoteAddr() != nil {
		participant.RemoteAddr = s.Conn.RemoteAddr().String()
	}

	return participant
}

func normalizeIdentityInput(v string) string {
	v = strings.TrimSpace(v)
	switch v {
	case "", "\"\"", "''", "-", "none":
		return ""
	default:
		return v
	}
}

func newToken(prefix string, byteCount int) (string, error) {
	b := make([]byte, byteCount)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return prefix + "_" + hex.EncodeToString(b), nil
}

func (m *Manager) Snapshot() ManagerSnapshot {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.snapshotLocked()
}

func (m *Manager) Subscribe() chan StadiumEvent {
	m.mu.Lock()
	defer m.mu.Unlock()
	ch := make(chan StadiumEvent, 10)
	m.subscribers[ch] = struct{}{}
	return ch
}

func (m *Manager) Unsubscribe(ch chan StadiumEvent) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.subscribers, ch)
	close(ch)
}

func (m *Manager) PublishArenaList() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.broadcastArenaListLocked()
}

func (m *Manager) broadcastArenaListLocked() {
	m.broadcastLocked("arena_list", m.listMatches())
	m.broadcastLocked("manager_state", m.snapshotLocked())
}

func (m *Manager) snapshotLocked() ManagerSnapshot {
	snapshot := ManagerSnapshot{
		GeneratedAt:     time.Now().UTC().Format(time.RFC3339Nano),
		NextArenaID:     m.nextArenaID,
		NextSessionID:   m.nextSessionID,
		NextMatchID:     m.nextMatchID,
		BotCount:        len(m.BotProfiles),
		MatchCount:      len(m.MatchHistory),
		SessionCount:    len(m.ActiveSessions),
		ArenaCount:      len(m.Arenas),
		SubscriberCount: len(m.subscribers),
		Sessions:        make([]SessionSnapshot, 0, len(m.ActiveSessions)),
		Arenas:          make([]ArenaSnapshot, 0, len(m.Arenas)),
		Bots:            make([]BotProfileSnapshot, 0, len(m.BotProfiles)),
		RecentMatches:   make([]MatchRecord, 0),
	}

	for _, sess := range m.ActiveSessions {
		session := SessionSnapshot{
			SessionID:    sess.SessionID,
			BotID:        sess.BotID,
			BotName:      sess.BotName,
			PlayerID:     sess.PlayerID,
			Capabilities: append([]string(nil), sess.Capabilities...),
			Wins:         sess.Wins,
			Losses:       sess.Losses,
			Draws:        sess.Draws,
			IsRegistered: sess.IsRegistered,
		}

		if sess.CurrentArena != nil {
			session.CurrentArenaID = sess.CurrentArena.ID
			session.HasCurrentArena = true
		}

		if sess.Conn != nil && sess.Conn.RemoteAddr() != nil {
			session.RemoteAddr = sess.Conn.RemoteAddr().String()
		}

		snapshot.Sessions = append(snapshot.Sessions, session)
	}

	sort.Slice(snapshot.Sessions, func(i, j int) bool {
		return snapshot.Sessions[i].SessionID < snapshot.Sessions[j].SessionID
	})

	for _, arena := range m.Arenas {
		arenaSnap := ArenaSnapshot{
			ID:            arena.ID,
			Status:        arena.Status,
			AllowHandicap: arena.AllowHandicap,
			TimeLimitMS:   arena.TimeLimit.Milliseconds(),
			Bot1TimeMS:    arena.Bot1Time.Milliseconds(),
			Bot2TimeMS:    arena.Bot2Time.Milliseconds(),
			MoveCount:     len(arena.MoveHistory),
			LastMove:      arena.LastMove.UTC().Format(time.RFC3339Nano),
			CreatedAt:     arena.CreatedAt.UTC().Format(time.RFC3339Nano),
			Observers:     make([]ObserverSnapshot, 0, len(arena.Observers)),
		}

		if !arena.ActivatedAt.IsZero() {
			arenaSnap.ActivatedAt = arena.ActivatedAt.UTC().Format(time.RFC3339Nano)
		}
		if !arena.CompletedAt.IsZero() {
			arenaSnap.CompletedAt = arena.CompletedAt.UTC().Format(time.RFC3339Nano)
		}

		if arena.Game != nil {
			arenaSnap.Game = arena.Game.GetName()
			arenaSnap.GameState = arena.Game.GetState()
		}

		if arena.Player1 != nil {
			arenaSnap.Player1Session = arena.Player1.SessionID
			arenaSnap.HasPlayer1 = true
			arenaSnap.Player1Name = arena.Player1.BotName
		}

		if arena.Player2 != nil {
			arenaSnap.Player2Session = arena.Player2.SessionID
			arenaSnap.HasPlayer2 = true
			arenaSnap.Player2Name = arena.Player2.BotName
		}

		for _, observer := range arena.Observers {
			if observer == nil {
				continue
			}
			arenaSnap.Observers = append(arenaSnap.Observers, ObserverSnapshot{
				SessionID: observer.SessionID,
				BotName:   observer.BotName,
			})
		}

		sort.Slice(arenaSnap.Observers, func(i, j int) bool {
			return arenaSnap.Observers[i].SessionID < arenaSnap.Observers[j].SessionID
		})
		arenaSnap.ObserverCount = len(arenaSnap.Observers)

		snapshot.Arenas = append(snapshot.Arenas, arenaSnap)
	}

	sort.Slice(snapshot.Arenas, func(i, j int) bool {
		return snapshot.Arenas[i].ID < snapshot.Arenas[j].ID
	})

	for _, profile := range m.BotProfiles {
		snapshot.Bots = append(snapshot.Bots, BotProfileSnapshot{
			BotID:             profile.BotID,
			DisplayName:       profile.DisplayName,
			CreatedAt:         profile.CreatedAt.UTC().Format(time.RFC3339Nano),
			LastSeenAt:        profile.LastSeenAt.UTC().Format(time.RFC3339Nano),
			RegistrationCount: profile.RegistrationCount,
			GamesPlayed:       profile.GamesPlayed,
			Wins:              profile.Wins,
			Losses:            profile.Losses,
			Draws:             profile.Draws,
		})
	}

	sort.Slice(snapshot.Bots, func(i, j int) bool {
		return snapshot.Bots[i].BotID < snapshot.Bots[j].BotID
	})

	recentStart := len(m.MatchHistory) - 25
	if recentStart < 0 {
		recentStart = 0
	}
	for i := recentStart; i < len(m.MatchHistory); i++ {
		snapshot.RecentMatches = append(snapshot.RecentMatches, m.MatchHistory[i])
	}

	return snapshot
}

func (m *Manager) broadcastLocked(eventType string, payload interface{}) {
	event := StadiumEvent{Type: eventType, Payload: payload}
	for ch := range m.subscribers {
		select {
		case ch <- event:
		default:
			// Client is too slow; avoid blocking the whole server
		}
	}
}
