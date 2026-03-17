package stadium

import (
	"errors"
	"strings"
	"time"
)

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
	arena.WinnerPlayerID = winnerPlayerID
	arena.IsDraw = isDraw

	record := MatchRecord{
		MatchID:        m.nextMatchID,
		ArenaID:        arena.ID,
		TerminalStatus: terminalStatus,
		EndReason:      endReason,
		WinnerPlayerID: winnerPlayerID,
		IsDraw:         isDraw,
		GameArgs:       append([]string(nil), arena.GameArgs...),
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
		if winnerPlayerID == 0 && arena.Player2 == nil && arena.Player1 != nil {
			if p, ok := m.BotProfiles[arena.Player1.BotID]; ok {
				p.Losses++
			}
		}
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

func (m *Manager) GetMatchRecord(matchID int) (MatchRecord, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()

	for i := len(m.MatchHistory) - 1; i >= 0; i-- {
		record := m.MatchHistory[i]
		if record.MatchID == matchID {
			return record, true
		}
	}

	return MatchRecord{}, false
}
