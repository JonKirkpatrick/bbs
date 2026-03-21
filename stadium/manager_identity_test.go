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

	if !result.IsNewIdentity {
		t.Fatalf("IsNewIdentity = false, want true")
	}
	if result.BotID == "" || result.BotSecret == "" {
		t.Fatalf("expected non-empty bot credentials in result: %+v", result)
	}
	if result.Authentication != "id+secret" {
		t.Fatalf("Authentication = %q, want %q", result.Authentication, "id+secret")
	}
	if s.SessionID == 0 || !s.IsRegistered {
		t.Fatalf("session registration flags not set: %+v", s)
	}
	if s.BotID != result.BotID {
		t.Fatalf("session BotID = %q, want %q", s.BotID, result.BotID)
	}
	if len(m.ActiveSessions) != 1 {
		t.Fatalf("ActiveSessions len = %d, want 1", len(m.ActiveSessions))
	}
	if _, ok := m.BotProfiles[result.BotID]; !ok {
		t.Fatalf("expected bot profile for %q", result.BotID)
	}
}

func TestRegisterSession_ExistingIdentityRequiresSecret(t *testing.T) {
	m := newTestManager()
	_, seed := newRegisteredSession(t, m, "seed")

	m.UnregisterSession(1)

	t.Run("missing secret", func(t *testing.T) {
		s := &Session{Conn: &testConn{}}
		_, err := m.RegisterSession(s, "alpha", seed.BotID, "", []string{"any"}, "")
		if err == nil {
			t.Fatal("expected error for missing secret")
		}
		if !strings.Contains(err.Error(), "bot_secret required") {
			t.Fatalf("unexpected error: %v", err)
		}
	})

	t.Run("invalid secret", func(t *testing.T) {
		s := &Session{Conn: &testConn{}}
		_, err := m.RegisterSession(s, "alpha", seed.BotID, "wrong_secret", []string{"any"}, "")
		if err == nil {
			t.Fatal("expected error for invalid secret")
		}
		if !strings.Contains(err.Error(), "invalid bot_secret") {
			t.Fatalf("unexpected error: %v", err)
		}
	})

	t.Run("valid secret", func(t *testing.T) {
		s := &Session{Conn: &testConn{}}
		result, err := m.RegisterSession(s, "alpha", seed.BotID, seed.BotSecret, []string{"any"}, "")
		if err != nil {
			t.Fatalf("RegisterSession returned unexpected error: %v", err)
		}
		if result.IsNewIdentity {
			t.Fatalf("IsNewIdentity = true, want false")
		}
		if result.BotSecret != "" {
			t.Fatalf("existing identity should not return bot secret, got %q", result.BotSecret)
		}
	})
}

func TestRegisterSession_RejectDuplicateConnectedBot(t *testing.T) {
	m := newTestManager()
	_, first := newRegisteredSession(t, m, "alpha")

	s2 := &Session{Conn: &testConn{}}
	_, err := m.RegisterSession(s2, "alpha2", first.BotID, first.BotSecret, []string{"any"}, "")
	if err == nil {
		t.Fatal("expected duplicate connected bot error")
	}
	if !strings.Contains(err.Error(), "bot already connected") {
		t.Fatalf("unexpected error: %v", err)
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
