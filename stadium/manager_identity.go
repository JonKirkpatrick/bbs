package stadium

import (
	"crypto/rand"
	"crypto/subtle"
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
	requestedBotID = normalizeIdentityInput(requestedBotID)
	providedSecret = normalizeIdentityInput(providedSecret)
	ownerToken = normalizeOwnerToken(ownerToken)

	if name == "" {
		return result, errors.New("name is required")
	}
	if ownerToken != "" && !isOwnerTokenValid(ownerToken) {
		return result, errors.New("owner token is invalid")
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
		if ownerToken != "" && sess.OwnerToken == ownerToken {
			return result, errors.New("owner token is already linked to another active session")
		}
	}

	// 2. Set ID and core flags
	s.SessionID = m.nextSessionID
	m.nextSessionID++
	s.IsRegistered = true
	s.BotID = profile.BotID
	s.OwnerToken = ownerToken
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

	if ownerToken != "" {
		result.Authentication = "id+secret+owner_token"
	}

	if newIdentity {
		result.BotSecret = profile.BotSecret
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

	if profile, ok := m.BotProfiles[sess.BotID]; ok {
		profile.DisplayName = sess.BotName
		profile.LastSeenAt = time.Now()
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
