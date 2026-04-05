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

func (m *Manager) sessionByOwnerTokenAndSessionIDLocked(ownerToken string, sessionID int) (*Session, bool) {
	ownerToken = normalizeOwnerToken(ownerToken)
	if ownerToken == "" || sessionID <= 0 {
		return nil, false
	}

	sess, ok := m.ActiveSessions[sessionID]
	if !ok || sess == nil {
		return nil, false
	}

	if sess.OwnerToken != ownerToken {
		return nil, false
	}

	return sess, true
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
	ownerToken = normalizeOwnerToken(ownerToken)
	if ownerToken == "" || !isOwnerTokenValid(ownerToken) {
		m.mu.Unlock()
		return 0, errors.New("owner token is invalid")
	}

	id := m.createArenaLocked(game, gameArgs, timeLimit, allowHandicap)
	m.mu.Unlock()
	m.PublishArenaList()
	return id, nil
}

func (m *Manager) JoinArenaForOwnerSession(ownerToken string, sessionID int, arenaID int, handicap int) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenAndSessionIDLocked(ownerToken, sessionID)
	if !ok {
		m.mu.Unlock()
		return errors.New("owner token is not authorized for the requested session")
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

func (m *Manager) LeaveArenaForOwnerSession(ownerToken string, sessionID int) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenAndSessionIDLocked(ownerToken, sessionID)
	if !ok {
		m.mu.Unlock()
		return errors.New("owner token is not authorized for the requested session")
	}
	m.mu.Unlock()

	if sess.CurrentArena == nil {
		return errors.New("bot is not currently in an arena")
	}
	m.leaveArena(sess)
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

func (m *Manager) EjectOwnerSessionBySessionID(ownerToken string, sessionID int, reason string) error {
	m.mu.Lock()
	sess, ok := m.sessionByOwnerTokenAndSessionIDLocked(ownerToken, sessionID)
	m.mu.Unlock()

	if !ok {
		return errors.New("owner token is not authorized for the requested session")
	}

	return m.EjectSession(sess.SessionID, reason)
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
