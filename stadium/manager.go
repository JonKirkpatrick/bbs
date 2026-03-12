package stadium

import (
	"fmt"
	"sync"

	"github.com/JonKirkpatrick/bbs/games"
)

type Manager struct {
	mu            sync.Mutex
	waitingPool   []*Session
	activeMatches []*Match
}

type Match struct {
	Player1 *Session
	Player2 *Session
	Game    games.GameInstance
}

var DefaultManager = &Manager{}

func (m *Manager) startMatch(p1, p2 *Session) {
	// For now, let's just hardcode connect4
	game, _ := games.GetGame("connect4")

	match := &Match{
		Player1: p1,
		Player2: p2,
		Game:    game,
	}

	p1.CurrentMatch = match
	p1.PlayerID = 1

	p2.CurrentMatch = match
	p2.PlayerID = 2

	p1.Conn.Write([]byte("BEGIN: Match started! You are Player 1.\n"))
	p2.Conn.Write([]byte("BEGIN: Match started! You are Player 2.\n"))
}

// NotifyOpponent sends a message to the player who is NOT the current actor
func (m *Match) NotifyOpponent(actorID int, message string) {
	var opponent *Session
	if actorID == 1 {
		opponent = m.Player2
	} else {
		opponent = m.Player1
	}

	if opponent != nil && opponent.Conn != nil {
		opponent.Conn.Write([]byte(fmt.Sprintf("UPDATE: %s\n", message)))
	}
}

// AddToWaitingRoom puts a bot in line and pairs them if someone else is waiting
func (m *Manager) AddToWaitingRoom(s *Session) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if len(m.waitingPool) > 0 {
		// Pair them up!
		opponent := m.waitingPool[0]
		m.waitingPool = m.waitingPool[1:]

		m.startMatch(opponent, s)
	} else {
		m.waitingPool = append(m.waitingPool, s)
		s.Conn.Write([]byte("WAIT: Looking for an opponent...\n"))
	}
}
