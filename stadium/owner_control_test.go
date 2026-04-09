package stadium

import (
	"strings"
	"testing"
	"time"
)

func TestNormalizeOwnerToken(t *testing.T) {
	if got := normalizeOwnerToken("  owner_x  "); got != "owner_x" {
		t.Fatalf("normalizeOwnerToken() = %q, want %q", got, "owner_x")
	}
}

func TestIsOwnerTokenValid(t *testing.T) {
	valid, err := NewOwnerToken()
	if err != nil {
		t.Fatalf("NewOwnerToken failed: %v", err)
	}
	if !isOwnerTokenValid(valid) {
		t.Fatalf("expected generated owner token to be valid: %q", valid)
	}
	if isOwnerTokenValid("owner_short") {
		t.Fatalf("expected short owner token to be invalid")
	}
	if isOwnerTokenValid("bot_not_owner") {
		t.Fatalf("expected non-owner prefix to be invalid")
	}
}

func TestOwnerSessionSnapshot(t *testing.T) {
	m := newTestManager()
	ownerToken, err := NewOwnerToken()
	if err != nil {
		t.Fatalf("NewOwnerToken failed: %v", err)
	}

	s := &Session{Conn: &testConn{}}
	_, err = m.RegisterSession(s, "alpha", []string{"any"}, ownerToken)
	if err != nil {
		t.Fatalf("RegisterSession failed: %v", err)
	}

	snap, ok := m.OwnerSessionSnapshot(ownerToken)
	if !ok {
		t.Fatalf("OwnerSessionSnapshot should find linked session")
	}
	if !snap.HasOwnerToken || snap.BotName != "alpha" {
		t.Fatalf("unexpected snapshot: %+v", snap)
	}

	if _, ok := m.OwnerSessionSnapshot("owner_missing_token_123456789012345678"); ok {
		t.Fatalf("expected missing owner token lookup to fail")
	}
}

func TestCreateArenaForOwner_RequiresValidOwnerToken(t *testing.T) {
	m := newTestManager()
	game := testGame{name: "owner_create", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true}

	ownerToken, err := NewOwnerToken()
	if err != nil {
		t.Fatalf("NewOwnerToken failed: %v", err)
	}

	arenaID, err := m.CreateArenaForOwner(ownerToken, game, nil, time.Second, false)
	if err != nil {
		t.Fatalf("CreateArenaForOwner failed: %v", err)
	}
	if arenaID <= 0 {
		t.Fatalf("expected valid arena id, got %d", arenaID)
	}

	_, err = m.CreateArenaForOwner("owner_short", game, nil, time.Second, false)
	if err == nil {
		t.Fatalf("expected invalid owner token to be rejected")
	}
	if !strings.Contains(err.Error(), "owner token") || !strings.Contains(err.Error(), "invalid") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestOwnerArenaControlFlow_CreateJoinLeave(t *testing.T) {
	m := newTestManager()
	ownerToken, err := NewOwnerToken()
	if err != nil {
		t.Fatalf("NewOwnerToken failed: %v", err)
	}

	s := &Session{Conn: &testConn{}}
	_, err = m.RegisterSession(s, "owner_bot", []string{"any"}, ownerToken)
	if err != nil {
		t.Fatalf("RegisterSession failed: %v", err)
	}

	game := testGame{name: "owner_game", requiredPlayers: 2, enforceMoveClock: true, supportsHandicap: true}
	arenaID, err := m.CreateArenaForOwner(ownerToken, game, []string{"seed=7"}, 1500*time.Millisecond, false)
	if err != nil {
		t.Fatalf("CreateArenaForOwner failed: %v", err)
	}

	if err := m.JoinArenaForOwner(ownerToken, arenaID, 0); err != nil {
		t.Fatalf("JoinArenaForOwner failed: %v", err)
	}
	if s.CurrentArena == nil || s.CurrentArena.ID != arenaID {
		t.Fatalf("owner session not attached to expected arena")
	}

	if err := m.LeaveArenaForOwner(ownerToken); err != nil {
		t.Fatalf("LeaveArenaForOwner failed: %v", err)
	}
	if s.CurrentArena != nil {
		t.Fatalf("owner session should be detached after leave")
	}
}
