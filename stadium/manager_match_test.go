package stadium

import (
	"testing"
	"time"
)

func TestRecordMove_AppendsHistory(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "match_test", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	p1, _ := newRegisteredSession(t, m, "p1")
	p2, _ := newRegisteredSession(t, m, "p2")
	if err := m.JoinArena(arenaID, p1, 0); err != nil {
		t.Fatalf("JoinArena p1 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, p2, 0); err != nil {
		t.Fatalf("JoinArena p2 returned unexpected error: %v", err)
	}

	if err := m.RecordMove(arenaID, p1, "A1", 250*time.Millisecond); err != nil {
		t.Fatalf("RecordMove returned unexpected error: %v", err)
	}

	arena := m.Arenas[arenaID]
	if arena == nil {
		t.Fatalf("arena %d not found", arenaID)
	}
	if got := len(arena.MoveHistory); got != 1 {
		t.Fatalf("MoveHistory length = %d, want 1", got)
	}
	move := arena.MoveHistory[0]
	if move.Number != 1 || move.PlayerID != 1 || move.Move != "A1" {
		t.Fatalf("unexpected move record: %+v", move)
	}
	if move.ElapsedMS != 250 {
		t.Fatalf("ElapsedMS = %d, want 250", move.ElapsedMS)
	}
}

func TestFinalizeArena_WinnerUpdatesProfiles(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "finalize_win", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true}
	arenaID := m.CreateArena(game, []string{"seed=1"}, time.Second, true)

	p1, r1 := newRegisteredSession(t, m, "p1")
	p2, r2 := newRegisteredSession(t, m, "p2")
	if err := m.JoinArena(arenaID, p1, 0); err != nil {
		t.Fatalf("JoinArena p1 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, p2, 0); err != nil {
		t.Fatalf("JoinArena p2 returned unexpected error: %v", err)
	}

	record, err := m.FinalizeArena(arenaID, "normal", 1, false)
	if err != nil {
		t.Fatalf("FinalizeArena returned unexpected error: %v", err)
	}

	if record.WinnerPlayerID != 1 || record.IsDraw {
		t.Fatalf("unexpected final record winner/draw: %+v", record)
	}
	if record.WinnerBotID != r1.BotID || record.WinnerBotName != "p1" {
		t.Fatalf("unexpected winner identity in record: %+v", record)
	}
	if record.Player1.BotID != r1.BotID || record.Player2.BotID != r2.BotID {
		t.Fatalf("participant identities not preserved in record: %+v", record)
	}

	profile1 := m.BotProfiles[r1.BotID]
	profile2 := m.BotProfiles[r2.BotID]
	if profile1.Wins != 1 || profile1.Losses != 0 || profile1.GamesPlayed != 1 {
		t.Fatalf("unexpected profile1 stats: %+v", profile1)
	}
	if profile2.Wins != 0 || profile2.Losses != 1 || profile2.GamesPlayed != 1 {
		t.Fatalf("unexpected profile2 stats: %+v", profile2)
	}
	if p1.PlayerID != 0 || p2.PlayerID != 0 || p1.CurrentArena != nil || p2.CurrentArena != nil {
		t.Fatalf("sessions were not detached after finalize")
	}
}

func TestFinalizeArena_DrawUpdatesProfiles(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "finalize_draw", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	_, r1 := newRegisteredSession(t, m, "p1")
	_, r2 := newRegisteredSession(t, m, "p2")

	s1 := m.ActiveSessions[1]
	s2 := m.ActiveSessions[2]
	if err := m.JoinArena(arenaID, s1, 0); err != nil {
		t.Fatalf("JoinArena s1 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, s2, 0); err != nil {
		t.Fatalf("JoinArena s2 returned unexpected error: %v", err)
	}

	record, err := m.FinalizeArena(arenaID, "draw", 0, true)
	if err != nil {
		t.Fatalf("FinalizeArena returned unexpected error: %v", err)
	}
	if !record.IsDraw || record.WinnerPlayerID != 0 {
		t.Fatalf("unexpected draw record: %+v", record)
	}

	profile1 := m.BotProfiles[r1.BotID]
	profile2 := m.BotProfiles[r2.BotID]
	if profile1.Draws != 1 || profile1.GamesPlayed != 1 {
		t.Fatalf("unexpected profile1 draw stats: %+v", profile1)
	}
	if profile2.Draws != 1 || profile2.GamesPlayed != 1 {
		t.Fatalf("unexpected profile2 draw stats: %+v", profile2)
	}
}

func TestGetMatchRecord_FoundAndNotFound(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "get_record", requiredPlayers: 1, enforceMoveClock: true, supportsHandicap: true}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	s, _ := newRegisteredSession(t, m, "solo")
	if err := m.JoinArena(arenaID, s, 0); err != nil {
		t.Fatalf("JoinArena returned unexpected error: %v", err)
	}

	record, err := m.FinalizeArena(arenaID, "solo_end", 0, false)
	if err != nil {
		t.Fatalf("FinalizeArena returned unexpected error: %v", err)
	}

	got, ok := m.GetMatchRecord(record.MatchID)
	if !ok {
		t.Fatalf("GetMatchRecord(%d) not found", record.MatchID)
	}
	if got.MatchID != record.MatchID || got.ArenaID != arenaID {
		t.Fatalf("unexpected match record lookup result: %+v", got)
	}

	if _, ok := m.GetMatchRecord(record.MatchID + 999); ok {
		t.Fatalf("expected missing match lookup to return ok=false")
	}
}
