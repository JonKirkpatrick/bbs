package stadium

import (
	"errors"
	"fmt"
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
	nextArenaID    int
	nextSessionID  int
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

// DefaultManager is the global instance of the Manager that handles all arenas and sessions in the stadium.
var DefaultManager = &Manager{}

// init initializes the DefaultManager and starts the watchdog goroutine for arena cleanup.
func init() {
	DefaultManager = &Manager{
		Arenas:         make(map[int]*Arena),
		nextArenaID:    1,
		ActiveSessions: make(map[int]*Session),
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
	}
}

// DestroyArena is a public method to forcefully remove an arena, typically called when a player leaves or a match ends.
func (m *Manager) DestroyArena(id int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.Arenas, id)
}

// NotifyOpponent sends a message to the opponent of the given actorID (1 or 2) in the arena.
func (m *Arena) NotifyOpponent(actorID int, message string) {
	var opponent *Session
	if actorID == 1 {
		opponent = m.Player2
	} else {
		opponent = m.Player1
	}

	if opponent != nil && opponent.Conn != nil {
		fmt.Fprintf(opponent.Conn, "UPDATE: %s\n", message)
	}
}

// NotifyAll sends a message to both players and all observers in the arena.
func (m *Arena) NotifyAll(msgType, payload string) {
	res := Response{
		Status:  "ok",
		Type:    msgType,
		Payload: payload,
	}

	// Notify Players
	m.Player1.SendJSON(res)
	m.Player2.SendJSON(res)

	// Notify Observers
	for _, obs := range m.Observers {
		obs.SendJSON(res)
	}
}

// ListMatches returns a summary of all current arenas, including their ID, game type, player names, and status, for display in the lobby.
func (m *Manager) ListMatches() []ArenaSummary {
	m.mu.Lock()
	defer m.mu.Unlock()

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
	}
}

// Broadcast sends a message to both players and all observers in the arena, prefixed with "OBSERVE:" for observers.
func (m *Arena) Broadcast(msg string) {
	// Notify players
	m.Player1.Conn.Write([]byte(msg + "\n"))
	m.Player2.Conn.Write([]byte(msg + "\n"))

	// Notify observers
	for _, obs := range m.Observers {
		obs.Conn.Write([]byte("OBSERVE: " + msg + "\n"))
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

// RegisterSession adds a new session to the manager's active sessions, ensuring that the bot name is unique.
func (m *Manager) RegisterSession(s *Session, name string) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	// 1. Check if name is already taken
	for _, sess := range m.ActiveSessions {
		if sess.BotName == name {
			return errors.New("bot name already in use")
		}
	}

	// 2. Assign ID and register
	s.SessionID = m.nextSessionID
	m.nextSessionID++
	s.BotName = name
	s.IsRegistered = true

	m.ActiveSessions[s.SessionID] = s
	return nil
}

// UnregisterSession removes a session from the manager's active sessions, typically called when a bot disconnects or quits.
func (m *Manager) UnregisterSession(sessionID int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.ActiveSessions, sessionID)
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
	return nil
}
