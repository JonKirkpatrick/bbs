package stadium

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"strings"
	"time"
)

// RegisterSession now captures the full profile of the bot upon entry.
func (m *Manager) RegisterSession(s *Session, name, requestedBotID, providedSecret string, caps []string, ownerToken string) (RegistrationResult, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var result RegistrationResult

	now := time.Now()
	ownerToken = normalizeOwnerToken(ownerToken)
	_ = normalizeIdentityInput(requestedBotID)
	_ = normalizeIdentityInput(providedSecret)

	if name == "" {
		return result, errors.New("name is required")
	}
	if ownerToken != "" && !isOwnerTokenValid(ownerToken) {
		return result, errors.New("owner token is invalid")
	}

	// 2. Set ID and core flags
	s.SessionID = m.nextSessionID
	m.nextSessionID++
	s.IsRegistered = true
	s.BotID = ""
	s.OwnerToken = ownerToken
	s.Capabilities = caps // Store what this bot can actually play
	s.PlayerID = 0
	s.CurrentArena = nil

	// 3. Identity assignment
	s.BotName = name
	s.Wins = 0
	s.Losses = 0
	s.Draws = 0

	m.ActiveSessions[s.SessionID] = s

	result = RegistrationResult{
		SessionID:      s.SessionID,
		Name:           s.BotName,
		GamesPlayed:    0,
		Wins:           0,
		Losses:         0,
		Draws:          0,
		RegisteredAt:   now.UTC().Format(time.RFC3339Nano),
		Authentication: "owner_token+control_token",
	}

	return result, nil
}

// UnregisterSession removes a session from the manager's active sessions, typically called when a bot disconnects or quits.
func (m *Manager) UnregisterSession(sessionID int) {
	m.mu.Lock()
	delete(m.ActiveSessions, sessionID)
	subscribers, events := m.prepareArenaListBroadcastLocked()
	m.mu.Unlock()
	m.publishEvents(subscribers, events)
}

// BotStatsForID returns the all-time W/L/D for a bot profile, used by the viewer.
func (m *Manager) BotStatsForID(botID string) (wins, losses, draws int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if p, ok := m.BotProfiles[botID]; ok {
		return p.Wins, p.Losses, p.Draws
	}
	return 0, 0, 0
}

func (m *Manager) EjectSession(sessionID int, reason string) error {
	m.mu.Lock()
	sess, exists := m.ActiveSessions[sessionID]
	if !exists {
		m.mu.Unlock()
		return errors.New("session not found")
	}
	m.mu.Unlock()

	if reason == "" {
		reason = "Removed by dashboard admin"
	}

	if sess.CurrentArena != nil {
		arena := sess.CurrentArena
		winnerPlayerID := 0
		status := "aborted"

		arena.mu.Lock()
		if arena.Player1 == sess && arena.Player2 != nil {
			winnerPlayerID = 2
			status = "completed"
		}
		if arena.Player2 == sess && arena.Player1 != nil {
			winnerPlayerID = 1
			status = "completed"
		}
		arena.mu.Unlock()

		arena.NotifyAll("error", "Player "+sess.BotName+" was ejected: "+reason)
		_, _ = m.finalizeArenaLocked(arena, "admin_eject", status, winnerPlayerID, false)
	}

	m.mu.Lock()
	delete(m.ActiveSessions, sessionID)
	m.mu.Unlock()
	m.PublishArenaList()

	if sess.Conn != nil {
		sess.SendJSON(Response{Status: "err", Type: "ejected", Payload: reason})
		sess.Conn.Close()
	}

	return nil
}

// UpdateSessionProfile allows a session to update its profile information, such as name or capabilities, while ensuring thread safety.
func (m *Manager) UpdateSessionProfile(sess *Session, key, val string) error {
	m.mu.Lock()

	switch key {
	case "name":
		sess.BotName = val
	case "capability":
		sess.Capabilities = append(sess.Capabilities, val)
	default:
		m.mu.Unlock()
		return errors.New("unknown field")
	}

	m.mu.Unlock()
	return nil
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
