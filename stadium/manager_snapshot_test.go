package stadium

import "testing"

func TestSnapshot_ContainsSessionsArenasHistory(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "snap_game", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true, state: `{"turn":1}`}
	arenaID := m.CreateArena(game, []string{"size=8"}, 1000, true)

	s1, r1 := newRegisteredSession(t, m, "p1")
	s2, r2 := newRegisteredSession(t, m, "p2")
	s3, _ := newRegisteredSession(t, m, "obs")

	if err := m.JoinArena(arenaID, s1, 0); err != nil {
		t.Fatalf("JoinArena s1 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, s2, 0); err != nil {
		t.Fatalf("JoinArena s2 returned unexpected error: %v", err)
	}
	if err := m.AddObserver(arenaID, s3); err != nil {
		t.Fatalf("AddObserver returned unexpected error: %v", err)
	}

	snapshot := m.Snapshot()

	if snapshot.SessionCount != 3 {
		t.Fatalf("SessionCount = %d, want 3", snapshot.SessionCount)
	}
	if snapshot.ArenaCount != 1 {
		t.Fatalf("ArenaCount = %d, want 1", snapshot.ArenaCount)
	}
	if snapshot.BotCount != 3 {
		t.Fatalf("BotCount = %d, want 3", snapshot.BotCount)
	}

	if len(snapshot.Sessions) != 3 {
		t.Fatalf("Sessions length = %d, want 3", len(snapshot.Sessions))
	}
	if snapshot.Sessions[0].SessionID > snapshot.Sessions[1].SessionID || snapshot.Sessions[1].SessionID > snapshot.Sessions[2].SessionID {
		t.Fatalf("sessions are not sorted by SessionID: %+v", snapshot.Sessions)
	}

	if len(snapshot.Arenas) != 1 {
		t.Fatalf("Arenas length = %d, want 1", len(snapshot.Arenas))
	}
	arena := snapshot.Arenas[0]
	if arena.ID != arenaID || !arena.HasPlayer1 || !arena.HasPlayer2 {
		t.Fatalf("unexpected arena snapshot: %+v", arena)
	}
	if arena.Player1Name != "p1" || arena.Player2Name != "p2" {
		t.Fatalf("unexpected arena player names: %+v", arena)
	}
	if arena.ObserverCount != 1 || len(arena.Observers) != 1 {
		t.Fatalf("unexpected observer snapshot: %+v", arena)
	}

	// Ensure bot snapshot sorting is deterministic.
	if len(snapshot.Bots) != 3 {
		t.Fatalf("Bots length = %d, want 3", len(snapshot.Bots))
	}
	if snapshot.Bots[0].BotID > snapshot.Bots[1].BotID || snapshot.Bots[1].BotID > snapshot.Bots[2].BotID {
		t.Fatalf("bot snapshots are not sorted by BotID: %+v", snapshot.Bots)
	}

	if _, ok := m.BotProfiles[r1.BotID]; !ok {
		t.Fatalf("missing profile for bot %q", r1.BotID)
	}
	if _, ok := m.BotProfiles[r2.BotID]; !ok {
		t.Fatalf("missing profile for bot %q", r2.BotID)
	}
}

func TestSnapshot_RecentMatchesLimitedTo25(t *testing.T) {
	m := newTestManager()

	for i := 1; i <= 30; i++ {
		m.MatchHistory = append(m.MatchHistory, MatchRecord{MatchID: i, ArenaID: i})
	}

	snapshot := m.Snapshot()
	if len(snapshot.RecentMatches) != 25 {
		t.Fatalf("RecentMatches length = %d, want 25", len(snapshot.RecentMatches))
	}
	if snapshot.RecentMatches[0].MatchID != 6 {
		t.Fatalf("first RecentMatches MatchID = %d, want 6", snapshot.RecentMatches[0].MatchID)
	}
	if snapshot.RecentMatches[24].MatchID != 30 {
		t.Fatalf("last RecentMatches MatchID = %d, want 30", snapshot.RecentMatches[24].MatchID)
	}
}
