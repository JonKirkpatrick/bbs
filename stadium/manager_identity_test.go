package stadium

import (
	"strings"
	"testing"
)

func TestRegisterSession_NewIdentity(t *testing.T) {
	m := newTestManager()
	s := &Session{Conn: &testConn{}}

	result, err := m.RegisterSession(s, "alpha", "", "", []string{"any", "grid"}, "")
	if err != nil {
		t.Fatalf("RegisterSession returned unexpected error: %v", err)
	}

	if result.Authentication != "owner_token+control_token" {
		t.Fatalf("Authentication = %q, want %q", result.Authentication, "owner_token+control_token")
	}
	if s.SessionID == 0 || !s.IsRegistered {
		t.Fatalf("session registration flags not set: %+v", s)
	}
	if s.BotID != "" {
		t.Fatalf("session BotID = %q, want empty", s.BotID)
	}
	if len(m.ActiveSessions) != 1 {
		t.Fatalf("ActiveSessions len = %d, want 1", len(m.ActiveSessions))
	}
	if len(m.BotProfiles) != 0 {
		t.Fatalf("BotProfiles len = %d, want 0", len(m.BotProfiles))
	}
}

func TestRegisterSession_AlwaysIssuesFreshRuntimeIdentity(t *testing.T) {
	m := newTestManager()
	_, seed := newRegisteredSession(t, m, "seed")

	m.UnregisterSession(1)
	s := &Session{Conn: &testConn{}}
	result, err := m.RegisterSession(s, "alpha", "", "", []string{"any"}, "")
	if err != nil {
		t.Fatalf("RegisterSession returned unexpected error: %v", err)
	}
	if result.SessionID == seed.SessionID {
		t.Fatalf("expected fresh runtime session identity, got same session id %d", result.SessionID)
	}
}

func TestRegisterSession_RejectInvalidOwnerToken(t *testing.T) {
	m := newTestManager()
	s := &Session{Conn: &testConn{}}

	_, err := m.RegisterSession(s, "alpha", "", "", []string{"any"}, "owner_short")
	if err == nil {
		t.Fatal("expected invalid owner token error")
	}
	if !strings.Contains(err.Error(), "owner token is invalid") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestUnregisterSession_RemovesActiveSession(t *testing.T) {
	m := newTestManager()
	s, _ := newRegisteredSession(t, m, "alpha")

	if len(m.ActiveSessions) != 1 {
		t.Fatalf("ActiveSessions len = %d, want 1", len(m.ActiveSessions))
	}

	m.UnregisterSession(s.SessionID)

	if len(m.ActiveSessions) != 0 {
		t.Fatalf("ActiveSessions len = %d, want 0", len(m.ActiveSessions))
	}
}

func TestUpdateSessionProfile_NameAndCapability(t *testing.T) {
	m := newTestManager()
	s, _ := newRegisteredSession(t, m, "alpha")

	if err := m.UpdateSessionProfile(s, "name", "renamed"); err != nil {
		t.Fatalf("UpdateSessionProfile name returned unexpected error: %v", err)
	}
	if s.BotName != "renamed" {
		t.Fatalf("BotName = %q, want %q", s.BotName, "renamed")
	}

	if err := m.UpdateSessionProfile(s, "capability", "grid"); err != nil {
		t.Fatalf("UpdateSessionProfile capability returned unexpected error: %v", err)
	}
	if got := s.Capabilities[len(s.Capabilities)-1]; got != "grid" {
		t.Fatalf("latest capability = %q, want %q", got, "grid")
	}

	if err := m.UpdateSessionProfile(s, "unknown", "x"); err == nil {
		t.Fatal("expected error for unknown field")
	}
}
