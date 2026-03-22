package stadium

import (
	"context"
	"testing"
	"time"
)

func TestInMemoryPersistenceStore_ServerIdentityRoundTrip(t *testing.T) {
	store := NewInMemoryPersistenceStore()
	ctx := context.Background()

	_, found, err := store.LoadServerIdentity(ctx)
	if err != nil {
		t.Fatalf("LoadServerIdentity returned error: %v", err)
	}
	if found {
		t.Fatal("did not expect identity before save")
	}

	identity := ServerIdentity{LocalServerID: "srv_1", RegistryStatus: "pending"}
	if err := store.SaveServerIdentity(ctx, identity); err != nil {
		t.Fatalf("SaveServerIdentity returned error: %v", err)
	}

	loaded, found, err := store.LoadServerIdentity(ctx)
	if err != nil {
		t.Fatalf("LoadServerIdentity returned error: %v", err)
	}
	if !found {
		t.Fatal("expected identity after save")
	}
	if loaded.LocalServerID != "srv_1" {
		t.Fatalf("unexpected local server id: %q", loaded.LocalServerID)
	}
}

func TestInMemoryPersistenceStore_InboxReceipt(t *testing.T) {
	store := NewInMemoryPersistenceStore()
	ctx := context.Background()

	has, err := store.HasInboxReceipt(ctx, "srv_a", "evt_1")
	if err != nil {
		t.Fatalf("HasInboxReceipt returned error: %v", err)
	}
	if has {
		t.Fatal("did not expect receipt before record")
	}

	receipt := DurableInboxReceipt{
		SourceServerID: "srv_a",
		SourceEventID:  "evt_1",
		ProcessedAt:    time.Now().UTC(),
	}
	if err := store.RecordInboxReceipt(ctx, receipt); err != nil {
		t.Fatalf("RecordInboxReceipt returned error: %v", err)
	}

	has, err = store.HasInboxReceipt(ctx, "srv_a", "evt_1")
	if err != nil {
		t.Fatalf("HasInboxReceipt returned error: %v", err)
	}
	if !has {
		t.Fatal("expected receipt after record")
	}
}
