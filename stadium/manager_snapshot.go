package stadium

import (
	"sort"
	"time"
)

func sessionSnapshotFromSession(sess *Session) SessionSnapshot {
	snapshot := SessionSnapshot{
		SessionID:     sess.SessionID,
		BotID:         sess.BotID,
		BotName:       sess.BotName,
		HasOwnerToken: sess.OwnerToken != "",
		PlayerID:      sess.PlayerID,
		Capabilities:  append([]string(nil), sess.Capabilities...),
		Wins:          sess.Wins,
		Losses:        sess.Losses,
		Draws:         sess.Draws,
		IsRegistered:  sess.IsRegistered,
	}

	if sess.CurrentArena != nil {
		snapshot.CurrentArenaID = sess.CurrentArena.ID
		snapshot.HasCurrentArena = true
	}

	if sess.Conn != nil && sess.Conn.RemoteAddr() != nil {
		snapshot.RemoteAddr = sess.Conn.RemoteAddr().String()
	}

	return snapshot
}

func (m *Manager) Snapshot() ManagerSnapshot {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.snapshotLocked()
}

func (m *Manager) snapshotLocked() ManagerSnapshot {
	snapshot := ManagerSnapshot{
		GeneratedAt:     time.Now().UTC().Format(time.RFC3339Nano),
		NextArenaID:     m.nextArenaID,
		NextSessionID:   m.nextSessionID,
		NextMatchID:     m.nextMatchID,
		BotCount:        len(m.BotProfiles),
		MatchCount:      len(m.MatchHistory),
		SessionCount:    len(m.ActiveSessions),
		ArenaCount:      len(m.Arenas),
		SubscriberCount: len(m.subscribers),
		Sessions:        make([]SessionSnapshot, 0, len(m.ActiveSessions)),
		Arenas:          make([]ArenaSnapshot, 0, len(m.Arenas)),
		Bots:            make([]BotProfileSnapshot, 0, len(m.BotProfiles)),
		RecentMatches:   make([]MatchRecord, 0),
	}

	for _, sess := range m.ActiveSessions {
		snapshot.Sessions = append(snapshot.Sessions, sessionSnapshotFromSession(sess))
	}

	sort.Slice(snapshot.Sessions, func(i, j int) bool {
		return snapshot.Sessions[i].SessionID < snapshot.Sessions[j].SessionID
	})

	for _, arena := range m.Arenas {
		arenaSnap := ArenaSnapshot{
			ID:                arena.ID,
			Status:            arena.Status,
			RequiredPlayers:   arena.RequiredPlayers,
			MoveClockEnabled:  arena.MoveClockEnabled,
			HandicapSupported: arena.HandicapSupported,
			AllowHandicap:     arena.AllowHandicap,
			Player1Handicap:   arena.Player1Handicap,
			Player2Handicap:   arena.Player2Handicap,
			TimeLimitMS:       arena.TimeLimit.Milliseconds(),
			Bot1TimeMS:        arena.Bot1Time.Milliseconds(),
			Bot2TimeMS:        arena.Bot2Time.Milliseconds(),
			MoveCount:         len(arena.MoveHistory),
			LastMove:          arena.LastMove.UTC().Format(time.RFC3339Nano),
			CreatedAt:         arena.CreatedAt.UTC().Format(time.RFC3339Nano),
			Observers:         make([]ObserverSnapshot, 0, len(arena.Observers)),
		}

		if !arena.ActivatedAt.IsZero() {
			arenaSnap.ActivatedAt = arena.ActivatedAt.UTC().Format(time.RFC3339Nano)
		}
		if !arena.CompletedAt.IsZero() {
			arenaSnap.CompletedAt = arena.CompletedAt.UTC().Format(time.RFC3339Nano)
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

	for _, profile := range m.BotProfiles {
		snapshot.Bots = append(snapshot.Bots, BotProfileSnapshot{
			BotID:             profile.BotID,
			DisplayName:       profile.DisplayName,
			CreatedAt:         profile.CreatedAt.UTC().Format(time.RFC3339Nano),
			LastSeenAt:        profile.LastSeenAt.UTC().Format(time.RFC3339Nano),
			RegistrationCount: profile.RegistrationCount,
			GamesPlayed:       profile.GamesPlayed,
			Wins:              profile.Wins,
			Losses:            profile.Losses,
			Draws:             profile.Draws,
		})
	}

	sort.Slice(snapshot.Bots, func(i, j int) bool {
		return snapshot.Bots[i].BotID < snapshot.Bots[j].BotID
	})

	recentStart := len(m.MatchHistory) - 25
	if recentStart < 0 {
		recentStart = 0
	}
	for i := recentStart; i < len(m.MatchHistory); i++ {
		snapshot.RecentMatches = append(snapshot.RecentMatches, m.MatchHistory[i])
	}

	return snapshot
}
