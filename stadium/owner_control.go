package stadium

import (
	"errors"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
)

func NewOwnerToken() (string, error) {
	return newToken("owner", 18)
}

func NewControlToken() (string, error) {
	return newToken("ctl", 18)
}

func normalizeOwnerToken(raw string) string {
	return strings.TrimSpace(raw)
}

func isOwnerTokenValid(token string) bool {
	return strings.HasPrefix(token, "owner_") && len(token) >= len("owner_")+24
}

func (m *Manager) sessionByOwnerTokenLocked(ownerToken string) (*Session, bool) {
	ownerToken = normalizeOwnerToken(ownerToken)
	if ownerToken == "" {
		return nil, false
	}

	for _, sess := range m.ActiveSessions {
		if sess != nil && sess.OwnerToken == ownerToken {
			return sess, true
		}
	}

	return nil, false
}

func (m *Manager) OwnerSessionSnapshot(ownerToken string) (SessionSnapshot, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()

	sess, ok := m.sessionByOwnerTokenLocked(ownerToken)
	if !ok {
		return SessionSnapshot{}, false
	}

	return sessionSnapshotFromSession(sess), true
}

func (m *Manager) CreateArenaForOwner(ownerToken string, game games.GameInstance, gameArgs []string, timeLimit time.Duration, allowHandicap bool) (int, error) {
	m.mu.Lock()
	if _, ok := m.sessionByOwnerTokenLocked(ownerToken); !ok {
		m.mu.Unlock()
		return 0, errors.New("no active session is linked to this dashboard token")
	}

	id := m.createArenaLocked(game, gameArgs, timeLimit, allowHandicap)
	m.mu.Unlock()
	m.PublishArenaList()
	return id, nil
}

func (m *Manager) JoinArenaForOwner(ownerToken string, arenaID int, handicap int) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenLocked(ownerToken)
	if !ok {
		m.mu.Unlock()
		return errors.New("no active session is linked to this dashboard token")
	}
	arena, exists := m.Arenas[arenaID]
	m.mu.Unlock()
	if !exists {
		return errors.New("arena not found")
	}

	if err := m.joinArena(arena, sess, handicap); err != nil {
		return err
	}
	m.PublishArenaList()
	return nil
}

// LeaveArenaForOwner removes the owner's bot from its current arena without
// closing the TCP connection — the bot stays registered and can rejoin.
func (m *Manager) LeaveArenaForOwner(ownerToken string) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenLocked(ownerToken)
	if !ok {
		m.mu.Unlock()
		return errors.New("no active session is linked to this dashboard token")
	}
	m.mu.Unlock()

	if sess.CurrentArena == nil {
		return errors.New("bot is not currently in an arena")
	}
	m.leaveArena(sess)
	m.PublishArenaList()
	return nil
}

func (m *Manager) EjectOwnerSession(ownerToken, reason string) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenLocked(ownerToken)
	m.mu.Unlock()

	if !ok {
		return errors.New("no active session is linked to this dashboard token")
	}

	return m.EjectSession(sess.SessionID, reason)
}
