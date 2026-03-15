package stadium

import (
	"errors"
	"fmt"
	"sort"
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
	subscribers    map[chan StadiumEvent]struct{} // For dashboard updates
	nextArenaID    int
	nextSessionID  int
}

type StadiumEvent struct {
	Type    string      `json:"type"`
	Payload interface{} `json:"payload"`
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
	BotName         string   `json:"bot_name"`
	PlayerID        int      `json:"player_id"`
	CurrentArenaID  int      `json:"current_arena_id,omitempty"`
	HasCurrentArena bool     `json:"has_current_arena"`
	Capabilities    []string `json:"capabilities"`
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
	LastMove       string             `json:"last_move"`
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
	GeneratedAt     string            `json:"generated_at"`
	NextArenaID     int               `json:"next_arena_id"`
	NextSessionID   int               `json:"next_session_id"`
	SessionCount    int               `json:"session_count"`
	ArenaCount      int               `json:"arena_count"`
	SubscriberCount int               `json:"subscriber_count"`
	Sessions        []SessionSnapshot `json:"sessions"`
	Arenas          []ArenaSnapshot   `json:"arenas"`
}

// DefaultManager is the global instance of the Manager that handles all arenas and sessions in the stadium.
var DefaultManager = &Manager{}

// init initializes the DefaultManager and starts the watchdog goroutine for arena cleanup.
func init() {
	DefaultManager = &Manager{
		Arenas:         make(map[int]*Arena),
		nextArenaID:    1,
		ActiveSessions: make(map[int]*Session),
		subscribers:    make(map[chan StadiumEvent]struct{}),
		nextSessionID:  1,
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
func (a *Arena) NotifyAll(msgType, payload string) {
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
		LastMove:      time.Now(),
	}
	m.broadcastArenaListLocked()
	return id
}

// JoinArena attempts to place a session into the specified arena as either Player 1 or Player 2,
// applying handicap settings if necessary. It returns an error if the arena is full or does not exist.
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
		arena.Status = "active"
		m.activateArena(arena)
	} else {
		return errors.New("arena full")
	}

	s.CurrentArena = arena
	m.broadcastArenaListLocked()
	return nil
}

// HandlePlayerLeave is called when a session disconnects or quits, ensuring that the arena is properly cleaned up and the opponent is notified.
func (m *Manager) HandlePlayerLeave(s *Session) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if s.CurrentArena != nil {
		// 1. Notify others
		s.CurrentArena.NotifyAll("error", "Player "+s.BotName+" left.")

		// 2. IMPORTANT: Clear references from the Arena to the Session
		if s.CurrentArena.Player1 == s {
			s.CurrentArena.Player1 = nil
		}
		if s.CurrentArena.Player2 == s {
			s.CurrentArena.Player2 = nil
		}

		// 3. Mark Arena as inactive/aborted
		s.CurrentArena.Status = "aborted"

		// 4. Sever the Session's reference to the arena
		s.CurrentArena = nil
		m.broadcastArenaListLocked()
	}
}

// activateArena sets the arena status to active,
// initializes player time based on handicap settings,
// and notifies both players that the game has started.
func (m *Manager) activateArena(a *Arena) {
	a.Status = "active"
	a.LastMove = time.Now()

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
func (m *Manager) RegisterSession(s *Session, name string, caps []string) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	// 1. Name uniqueness check
	for _, sess := range m.ActiveSessions {
		if sess.BotName == name {
			return errors.New("bot name already in use")
		}
	}

	// 2. Set ID and core flags
	s.SessionID = m.nextSessionID
	m.nextSessionID++
	s.IsRegistered = true
	s.Capabilities = caps // Store what this bot can actually play

	// 3. Identity assignment
	s.BotName = name

	m.ActiveSessions[s.SessionID] = s
	m.broadcastArenaListLocked()
	return nil
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

		if arena.Player1 == sess {
			arena.Player1 = nil
		}
		if arena.Player2 == sess {
			arena.Player2 = nil
		}

		arena.Status = "aborted"
		sess.CurrentArena = nil
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
	m.broadcastArenaListLocked()
	return nil
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
		SessionCount:    len(m.ActiveSessions),
		ArenaCount:      len(m.Arenas),
		SubscriberCount: len(m.subscribers),
		Sessions:        make([]SessionSnapshot, 0, len(m.ActiveSessions)),
		Arenas:          make([]ArenaSnapshot, 0, len(m.Arenas)),
	}

	for _, sess := range m.ActiveSessions {
		session := SessionSnapshot{
			SessionID:    sess.SessionID,
			BotName:      sess.BotName,
			PlayerID:     sess.PlayerID,
			Capabilities: append([]string(nil), sess.Capabilities...),
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
			LastMove:      arena.LastMove.UTC().Format(time.RFC3339Nano),
			Observers:     make([]ObserverSnapshot, 0, len(arena.Observers)),
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
