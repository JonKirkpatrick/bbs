package stadium

import (
	"errors"
	"strings"
	"time"
)

func (m *Manager) RecordMove(arenaID int, actor *Session, move string, elapsed time.Duration) error {
	m.mu.Lock()
	arena, exists := m.Arenas[arenaID]
	m.mu.Unlock()
	if !exists {
		return errors.New("arena not found")
	}

	now := time.Now()
	arena.mu.Lock()
	rec := MatchMove{
		Number:     len(arena.MoveHistory) + 1,
		PlayerID:   actor.PlayerID,
		SessionID:  actor.SessionID,
		BotID:      actor.BotID,
		BotName:    actor.BotName,
		Move:       move,
		ElapsedMS:  elapsed.Milliseconds(),
		OccurredAt: now.UTC().Format(time.RFC3339Nano),
	}
	arena.MoveHistory = append(arena.MoveHistory, rec)
	arena.LastMove = now
	arena.mu.Unlock()

	m.PublishArenaList()
	return nil
}

func (m *Manager) FinalizeArena(arenaID int, endReason string, winnerPlayerID int, isDraw bool) (MatchRecord, error) {
	m.mu.Lock()
	arena, exists := m.Arenas[arenaID]
	m.mu.Unlock()
	if !exists {
		return MatchRecord{}, errors.New("arena not found")
	}

	record, err := m.finalizeArenaLocked(arena, endReason, "completed", winnerPlayerID, isDraw)
	if err != nil {
		return MatchRecord{}, err
	}

	m.PublishArenaList()
	return record, nil
}

func (m *Manager) finalizeArenaLocked(arena *Arena, endReason, terminalStatus string, winnerPlayerID int, isDraw bool) (MatchRecord, error) {
	if arena == nil {
		return MatchRecord{}, errors.New("arena is nil")
	}

	now := time.Now()

	arena.mu.Lock()
	if arena.CompletedAt.IsZero() == false {
		arena.mu.Unlock()
		return MatchRecord{}, errors.New("arena already finalized")
	}

	arena.Status = terminalStatus
	arena.CompletedAt = now
	arena.LastMove = now
	arena.WinnerPlayerID = winnerPlayerID
	arena.IsDraw = isDraw

	player1 := arena.Player1
	player2 := arena.Player2
	observers := append([]*Session(nil), arena.Observers...)

	record := MatchRecord{
		ArenaID:        arena.ID,
		TerminalStatus: terminalStatus,
		EndReason:      endReason,
		WinnerPlayerID: winnerPlayerID,
		IsDraw:         isDraw,
		GameArgs:       append([]string(nil), arena.GameArgs...),
		StartedAt:      arena.CreatedAt.UTC().Format(time.RFC3339Nano),
		EndedAt:        now.UTC().Format(time.RFC3339Nano),
		Observers:      make([]ObserverSnapshot, 0, len(observers)),
		Moves:          append([]MatchMove(nil), arena.MoveHistory...),
	}

	if !arena.ActivatedAt.IsZero() {
		record.StartedAt = arena.ActivatedAt.UTC().Format(time.RFC3339Nano)
	}

	if arena.Game != nil {
		record.Game = arena.Game.GetName()
		record.FinalGameState = arena.Game.GetState()
	}

	record.Player1 = participantFromSession(player1)
	record.Player2 = participantFromSession(player2)

	for _, observer := range observers {
		if observer == nil {
			continue
		}
		record.Observers = append(record.Observers, ObserverSnapshot{
			SessionID: observer.SessionID,
			BotName:   observer.BotName,
		})
	}

	record.MoveSequence = make([]string, 0, len(record.Moves))
	for _, move := range record.Moves {
		record.MoveSequence = append(record.MoveSequence, move.Move)
	}
	record.MoveCount = len(record.MoveSequence)
	record.CompactMoves = strings.Join(record.MoveSequence, ",")

	winnerName := ""
	if winnerPlayerID == 1 && player1 != nil {
		record.WinnerBotID = player1.BotID
		winnerName = player1.BotName
	}
	if winnerPlayerID == 2 && player2 != nil {
		record.WinnerBotID = player2.BotID
		winnerName = player2.BotName
	}
	record.WinnerBotName = winnerName

	if player1 != nil {
		player1.CurrentArena = nil
		player1.PlayerID = 0
	}
	if player2 != nil {
		player2.CurrentArena = nil
		player2.PlayerID = 0
	}
	for _, observer := range observers {
		if observer != nil {
			observer.CurrentArena = nil
		}
	}

	arena.Player1 = nil
	arena.Player2 = nil
	arena.Observers = nil
	arena.mu.Unlock()

	m.mu.Lock()
	record.MatchID = m.nextMatchID
	m.nextMatchID++
	m.applyOutcomeToSessionsLocked(player1, player2, winnerPlayerID, isDraw)
	m.MatchHistory = append(m.MatchHistory, record)
	m.mu.Unlock()
	m.persistMatchRecord(record)

	return record, nil
}

func (m *Manager) applyOutcomeToSessionsLocked(player1, player2 *Session, winnerPlayerID int, isDraw bool) {
	participants := make([]*Session, 0, 2)
	if player1 != nil {
		participants = append(participants, player1)
	}
	if player2 != nil {
		participants = append(participants, player2)
	}

	if isDraw {
		for _, participant := range participants {
			participant.Draws++
		}
		return
	}

	if winnerPlayerID == 0 && player1 != nil && player2 == nil {
		player1.Losses++
		return
	}

	if winnerPlayerID == 1 {
		if player1 != nil {
			player1.Wins++
		}
		if player2 != nil {
			player2.Losses++
		}
		return
	}

	if winnerPlayerID == 2 {
		if player2 != nil {
			player2.Wins++
		}
		if player1 != nil {
			player1.Losses++
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
