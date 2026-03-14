package stadium

import (
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
)

type Manager struct {
	mu             sync.Mutex
	Arenas         map[int]*Arena
	ActiveSessions map[int]*Session
	nextArenaID    int
	nextSessionID  int
}

type Arena struct {
	ID            int
	Player1       *Session
	Player2       *Session
	Observers     []*Session
	AllowHandicap bool
	Status        string // "waiting", "active", "completed"
	Game          games.GameInstance
	TimeLimit     time.Duration
	Bot1Time      time.Duration
	Bot2Time      time.Duration
	LastMove      time.Time
}

var DefaultManager = &Manager{}

func init() {
	DefaultManager = &Manager{
		Arenas:         make(map[int]*Arena),
		nextArenaID:    1,
		ActiveSessions: make(map[int]*Session),
		nextSessionID:  1,
	}
}

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

func (m *Manager) ListMatches() string {
	m.mu.Lock()
	defer m.mu.Unlock()

	var sb strings.Builder
	sb.WriteString("CURRENT_ARENAS:\n")

	for id, arena := range m.Arenas {
		p1Name := "Waiting..."
		if arena.Player1 != nil {
			p1Name = arena.Player1.BotName
		}

		p2Name := "Waiting..."
		if arena.Player2 != nil {
			p2Name = arena.Player2.BotName
		}

		sb.WriteString(fmt.Sprintf("%d: %s vs %s\n", id, p1Name, p2Name))
	}
	return sb.String()
}

// Inside stadium/manager.go

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

func (m *Manager) CreateArena(gameType string, timeLimit time.Duration, allowHandicap bool) int {
	m.mu.Lock()
	defer m.mu.Unlock()

	game, _ := games.GetGame(gameType)
	id := m.nextArenaID
	m.nextArenaID++

	m.Arenas[id] = &Arena{
		ID:            id,
		Game:          game,
		TimeLimit:     timeLimit,
		AllowHandicap: allowHandicap,
		Status:        "waiting",
		Observers:     make([]*Session, 0),
	}
	return id
}

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
		arena.Status = "active" // Start the game
		m.activateArena(arena)
	} else {
		return errors.New("arena full")
	}

	// Logic for applying handicap based on arena.AllowHandicap...

	s.CurrentArena = arena
	return nil
}

func (m *Arena) Broadcast(msg string) {
	// Notify players
	m.Player1.Conn.Write([]byte(msg + "\n"))
	m.Player2.Conn.Write([]byte(msg + "\n"))

	// Notify observers
	for _, obs := range m.Observers {
		obs.Conn.Write([]byte("OBSERVE: " + msg + "\n"))
	}
}

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
