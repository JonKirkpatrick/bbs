package stadium

import "testing"

func TestSnapshot_ContainsSessionsArenasHistory(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "snap_game", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true, state: `{"turn":1}`}
	arenaID := m.CreateArena(game, []string{"size=8"}, 1000, true)

	s1, _ := newRegisteredSession(t, m, "p1")
	s2, _ := newRegisteredSession(t, m, "p2")
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
	if snapshot.BotCount != 0 {
		t.Fatalf("BotCount = %d, want 0", snapshot.BotCount)
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

	if len(snapshot.Bots) != 0 {
		t.Fatalf("Bots length = %d, want 0", len(snapshot.Bots))
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
