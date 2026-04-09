package stadium

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestBootstrapServerIdentity_CreatesAndRegisters(t *testing.T) {
	m := newTestManager()
	store := NewInMemoryPersistenceStore()
	m.SetPersistenceStore(store)
	m.SetGlobalServerRegistrar(NewMockGlobalServerRegistrar())

	identity, err := m.BootstrapServerIdentity(context.Background(), "My Local Node", "v0.0.0")
	if err != nil {
		t.Fatalf("BootstrapServerIdentity returned error: %v", err)
	}
	if identity.LocalServerID == "" {
		t.Fatal("expected local server id")
	}
	if identity.GlobalServerID == "" {
		t.Fatal("expected global server id")
	}
	if identity.RegistryStatus != "active" {
		t.Fatalf("expected active status, got %q", identity.RegistryStatus)
	}

	loaded, found, err := store.LoadServerIdentity(context.Background())
	if err != nil {
		t.Fatalf("LoadServerIdentity returned error: %v", err)
	}
	if !found {
		t.Fatal("expected identity to be persisted")
	}
	if loaded.LocalServerID != identity.LocalServerID {
		t.Fatalf("loaded local id mismatch: got %q want %q", loaded.LocalServerID, identity.LocalServerID)
	}
}

func TestRegisterSession_DoesNotPersistProfile(t *testing.T) {
	m := newTestManager()
	store := NewInMemoryPersistenceStore()
	m.SetPersistenceStore(store)

	session := &Session{}
	_, err := m.RegisterSession(session, "bot-alpha", []string{"any"}, "")
	if err != nil {
		t.Fatalf("RegisterSession returned error: %v", err)
	}

	if len(store.botProfiles) != 0 {
		t.Fatalf("expected no persisted bot profiles, found %d", len(store.botProfiles))
	}
}

type flakyOutboxPublisher struct {
	fails int
}

func (p *flakyOutboxPublisher) PublishOutboxEvent(_ context.Context, _ DurableOutboxEvent) error {
	if p.fails > 0 {
		p.fails--
		return errors.New("temporary publish failure")
	}
	return nil
}

func TestOutboxWorker_RetryThenPublish(t *testing.T) {
	m := newTestManager()
	store := NewInMemoryPersistenceStore()
	m.SetPersistenceStore(store)
	publisher := &flakyOutboxPublisher{fails: 1}
	m.SetOutboxPublisher(publisher)

	event := DurableOutboxEvent{
		EventID:       "evt_1",
		EventType:     "match_finalized",
		PayloadJSON:   `{"match_id":1}`,
		CreatedAt:     time.Now().UTC(),
		NextAttemptAt: time.Now().UTC(),
		PublishStatus: "pending",
	}
	if err := store.AppendOutboxEvent(context.Background(), event); err != nil {
		t.Fatalf("AppendOutboxEvent returned error: %v", err)
	}

	m.processOutboxBatch(10)
	afterFail := store.outbox[event.EventID]
	if afterFail.PublishStatus != "retry" {
		t.Fatalf("expected retry status after failure, got %q", afterFail.PublishStatus)
	}
	if afterFail.RetryCount != 1 {
		t.Fatalf("expected retry count 1, got %d", afterFail.RetryCount)
	}

	updated := afterFail
	updated.NextAttemptAt = time.Now().UTC().Add(-time.Second)
	store.outbox[event.EventID] = updated

	m.processOutboxBatch(10)
	afterSuccess := store.outbox[event.EventID]
	if afterSuccess.PublishStatus != "published" {
		t.Fatalf("expected published status, got %q", afterSuccess.PublishStatus)
	}
	if afterSuccess.PublishedAt.IsZero() {
		t.Fatal("expected published timestamp")
	}
}

func TestRecentDurableMatchesForBot(t *testing.T) {
	m := newTestManager()
	store := NewInMemoryPersistenceStore()
	m.SetPersistenceStore(store)

	err := store.AppendMatch(context.Background(), DurableMatch{
		MatchID:        1,
		ArenaID:        1,
		Game:           "counter",
		TerminalStatus: "completed",
		EndReason:      "done",
		StartedAt:      time.Now().UTC().Add(-time.Minute),
		EndedAt:        time.Now().UTC(),
	}, []DurableMatchMove{
		{MatchID: 1, Sequence: 1, BotID: "bot_a", Move: "x", OccurredAt: time.Now().UTC()},
		{MatchID: 1, Sequence: 2, BotID: "bot_b", Move: "y", OccurredAt: time.Now().UTC()},
	})
	if err != nil {
		t.Fatalf("AppendMatch returned error: %v", err)
	}

	matches, err := m.RecentDurableMatchesForBot(context.Background(), "bot_b", 5)
	if err != nil {
		t.Fatalf("RecentDurableMatchesForBot returned error: %v", err)
	}
	if len(matches) != 1 {
		t.Fatalf("expected 1 match, got %d", len(matches))
	}
	if matches[0].MatchID != 1 {
		t.Fatalf("expected match id 1, got %d", matches[0].MatchID)
	}
}
