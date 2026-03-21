package stadium

import (
	"strings"
	"testing"
	"time"
)

func TestCreateArena_AssignsIDAndPublishesState(t *testing.T) {
	m := newTestManager()
	game := testGame{
		name:             "arena_game",
		requiredPlayers:  2,
		enforceMoveClock: true,
		supportsHandicap: true,
	}

	id := m.CreateArena(game, []string{"k=v"}, 1500*time.Millisecond, true)
	if id != 1 {
		t.Fatalf("CreateArena id = %d, want 1", id)
	}

	arena, ok := m.Arenas[id]
	if !ok {
		t.Fatalf("arena %d not found", id)
	}
	if arena.Status != "waiting" {
		t.Fatalf("arena status = %q, want %q", arena.Status, "waiting")
	}
	if !arena.MoveClockEnabled || !arena.HandicapSupported || !arena.AllowHandicap {
		t.Fatalf("arena policy flags not set as expected: %+v", arena)
	}
	if arena.TimeLimit != 1500*time.Millisecond {
		t.Fatalf("arena time limit = %v, want %v", arena.TimeLimit, 1500*time.Millisecond)
	}
}

func TestCreateArena_ZeroPlayerAutoActivates(t *testing.T) {
	m := newTestManager()
	game := testGame{
		name:             "zero_player",
		requiredPlayers:  0,
		enforceMoveClock: false,
		supportsHandicap: false,
	}

	id := m.CreateArena(game, nil, time.Second, false)
	arena := m.Arenas[id]
	if arena == nil {
		t.Fatalf("arena %d not found", id)
	}
	if arena.Status != "active" {
		t.Fatalf("arena status = %q, want %q", arena.Status, "active")
	}
}

func TestJoinArena_ValidatesHandicap(t *testing.T) {
	m := newTestManager()
	game := testGame{
		name:             "join_test",
		requiredPlayers:  2,
		enforceMoveClock: true,
		supportsHandicap: true,
	}
	arenaID := m.CreateArena(game, nil, time.Second, false)

	sBad, _ := newRegisteredSession(t, m, "bad")
	err := m.JoinArena(arenaID, sBad, 10)
	if err == nil {
		t.Fatal("expected handicap validation error when arena disallows handicap")
	}
	if !strings.Contains(err.Error(), "does not allow handicap") {
		t.Fatalf("unexpected error: %v", err)
	}

	sGood, _ := newRegisteredSession(t, m, "good")
	if err := m.JoinArena(arenaID, sGood, 0); err != nil {
		t.Fatalf("JoinArena with zero handicap returned unexpected error: %v", err)
	}
	if sGood.PlayerID != 1 {
		t.Fatalf("PlayerID = %d, want 1", sGood.PlayerID)
	}
}

func TestJoinArena_RejectsWhenFull(t *testing.T) {
	m := newTestManager()
	game := testGame{
		name:             "full_test",
		requiredPlayers:  2,
		enforceMoveClock: true,
		supportsHandicap: true,
	}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	s1, _ := newRegisteredSession(t, m, "p1")
	s2, _ := newRegisteredSession(t, m, "p2")
	s3, _ := newRegisteredSession(t, m, "p3")

	if err := m.JoinArena(arenaID, s1, 0); err != nil {
		t.Fatalf("JoinArena p1 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, s2, 0); err != nil {
		t.Fatalf("JoinArena p2 returned unexpected error: %v", err)
	}
	if err := m.JoinArena(arenaID, s3, 0); err == nil {
		t.Fatal("expected arena full error")
	}
}

func TestLeaveArena_PlayerAndObserverPaths(t *testing.T) {
	m := newTestManager()
	game := testGame{
		name:             "leave_test",
		requiredPlayers:  2,
		enforceMoveClock: true,
		supportsHandicap: true,
	}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	player, _ := newRegisteredSession(t, m, "player")
	observer, _ := newRegisteredSession(t, m, "observer")

	if err := m.JoinArena(arenaID, player, 0); err != nil {
		t.Fatalf("JoinArena player returned unexpected error: %v", err)
	}
	if err := m.AddObserver(arenaID, observer); err != nil {
		t.Fatalf("AddObserver returned unexpected error: %v", err)
	}

	m.LeaveArena(observer)
	if observer.CurrentArena != nil {
		t.Fatalf("observer should be detached from arena")
	}
	arena := m.Arenas[arenaID]
	if len(arena.Observers) != 0 {
		t.Fatalf("observer list len = %d, want 0", len(arena.Observers))
	}

	m.LeaveArena(player)
	if player.CurrentArena != nil {
		t.Fatalf("player should be detached from waiting arena")
	}
	if player.PlayerID != 0 {
		t.Fatalf("player PlayerID = %d, want 0", player.PlayerID)
	}
	if arena.Player1 != nil {
		t.Fatalf("arena player1 should be nil after leave")
	}
}

func TestNormalizeArenaHandicap_BoundsAndPolicy(t *testing.T) {
	tests := []struct {
		name          string
		handicap      int
		allowHandicap bool
		wantValue     int
		wantErr       bool
	}{
		{name: "disabled and zero", handicap: 0, allowHandicap: false, wantValue: 0, wantErr: false},
		{name: "disabled and non-zero", handicap: 1, allowHandicap: false, wantValue: 0, wantErr: true},
		{name: "lower bound", handicap: -90, allowHandicap: true, wantValue: -90, wantErr: false},
		{name: "upper bound", handicap: 300, allowHandicap: true, wantValue: 300, wantErr: false},
		{name: "too low", handicap: -91, allowHandicap: true, wantValue: 0, wantErr: true},
		{name: "too high", handicap: 301, allowHandicap: true, wantValue: 0, wantErr: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := normalizeArenaHandicap(tc.handicap, tc.allowHandicap)
			if tc.wantErr {
				if err == nil {
					t.Fatalf("normalizeArenaHandicap(%d, %v) expected error", tc.handicap, tc.allowHandicap)
				}
				return
			}
			if err != nil {
				t.Fatalf("normalizeArenaHandicap(%d, %v) returned unexpected error: %v", tc.handicap, tc.allowHandicap, err)
			}
			if got != tc.wantValue {
				t.Fatalf("normalizeArenaHandicap(%d, %v) = %d, want %d", tc.handicap, tc.allowHandicap, got, tc.wantValue)
			}
		})
	}
}
