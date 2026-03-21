package stadium

import (
	"strings"
	"testing"
	"time"
)

func TestGetHelpText(t *testing.T) {
	unregistered := GetHelpText(false)
	if !strings.Contains(unregistered, "REGISTER") || !strings.Contains(unregistered, "QUIT") {
		t.Fatalf("unregistered help missing expected commands: %q", unregistered)
	}
	if strings.Contains(unregistered, "CREATE") {
		t.Fatalf("unregistered help should not include CREATE: %q", unregistered)
	}

	registered := GetHelpText(true)
	for _, cmd := range []string{"LIST", "CREATE", "JOIN", "WATCH", "MOVE", "QUIT"} {
		if !strings.Contains(registered, cmd) {
			t.Fatalf("registered help missing %q: %q", cmd, registered)
		}
	}
}

func TestGetArenaViewerState_FoundAndMissing(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "viewer_game", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true, state: `{"turn":1}`}
	arenaID := m.CreateArena(game, nil, time.Second, true)

	s1, _ := newRegisteredSession(t, m, "p1")
	s2, _ := newRegisteredSession(t, m, "p2")

	if err := m.JoinArena(arenaID, s1, 0); err != nil {
		t.Fatalf("JoinArena s1 failed: %v", err)
	}
	if err := m.JoinArena(arenaID, s2, 0); err != nil {
		t.Fatalf("JoinArena s2 failed: %v", err)
	}

	state, ok := m.GetArenaViewerState(arenaID)
	if !ok {
		t.Fatalf("GetArenaViewerState(%d) should exist", arenaID)
	}
	if state.ArenaID != arenaID || state.Game != "viewer_game" {
		t.Fatalf("unexpected viewer state identity: %+v", state)
	}
	if state.Player1.Name != "p1" || state.Player2.Name != "p2" {
		t.Fatalf("unexpected viewer players: %+v", state)
	}
	if state.MoveCount != 0 || state.Status != "active" {
		t.Fatalf("unexpected viewer status/moves: %+v", state)
	}

	if _, ok := m.GetArenaViewerState(arenaID + 999); ok {
		t.Fatalf("missing arena should return ok=false")
	}
}
