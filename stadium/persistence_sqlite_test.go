package stadium

import (
	"context"
	"database/sql"
	"fmt"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	_ "modernc.org/sqlite"
)

func TestSQLitePersistenceStore_RetriesWhenDatabaseIsLocked(t *testing.T) {
	t.Parallel()

	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "stadium.sqlite3")

	store, err := NewSQLitePersistenceStore(dbPath)
	if err != nil {
		t.Fatalf("NewSQLitePersistenceStore returned error: %v", err)
	}
	defer func() {
		if closeErr := store.Close(); closeErr != nil {
			t.Fatalf("Close returned error: %v", closeErr)
		}
	}()

	lockerDB, err := sql.Open("sqlite", dbPath+"?_pragma=journal_mode=WAL")
	if err != nil {
		t.Fatalf("sql.Open returned error: %v", err)
	}
	defer func() {
		if closeErr := lockerDB.Close(); closeErr != nil {
			t.Fatalf("locker DB close returned error: %v", closeErr)
		}
	}()

	ctx := context.Background()
	tx, err := lockerDB.BeginTx(ctx, nil)
	if err != nil {
		t.Fatalf("BeginTx returned error: %v", err)
	}

	_, err = tx.ExecContext(ctx, `
		INSERT OR REPLACE INTO federation_outbox (
			event_id, origin_server_id, event_type, payload_json, created_at,
			published_at, next_attempt_at, publish_status, retry_count, last_error
		)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
	`,
		"lock-holder",
		"srv-a",
		"test",
		`{"event":"lock"}`,
		time.Now().UTC().Format(time.RFC3339Nano),
		"",
		time.Now().UTC().Format(time.RFC3339Nano),
		"pending",
		0,
		"",
	)
	if err != nil {
		t.Fatalf("locker insert returned error: %v", err)
	}

	errCh := make(chan error, 1)
	go func() {
		errCh <- store.AppendOutboxEvent(ctx, DurableOutboxEvent{
			EventID:        "contended-event",
			OriginServerID: "srv-b",
			EventType:      "test",
			PayloadJSON:    `{"event":"contended"}`,
			CreatedAt:      time.Now().UTC(),
			NextAttemptAt:  time.Now().UTC(),
			PublishStatus:  "pending",
		})
	}()

	// Hold a write lock slightly past busy_timeout so the first attempt times out.
	time.Sleep(5200 * time.Millisecond)
	if err := tx.Commit(); err != nil {
		t.Fatalf("Commit returned error: %v", err)
	}

	select {
	case err := <-errCh:
		if err != nil {
			t.Fatalf("AppendOutboxEvent returned error after lock release: %v", err)
		}
	case <-time.After(4 * time.Second):
		t.Fatal("timed out waiting for contended write")
	}

	events, err := store.ListPendingOutboxEvents(ctx, 100, time.Now().UTC().Add(time.Minute))
	if err != nil {
		t.Fatalf("ListPendingOutboxEvents returned error: %v", err)
	}
	found := false
	for _, event := range events {
		if event.EventID == "contended-event" {
			found = true
			break
		}
	}
	if !found {
		t.Fatal("expected contended-event to be persisted")
	}
}

func TestSQLitePersistenceStore_ConcurrentWritesAndReads_NoBusyErrors(t *testing.T) {
	t.Parallel()

	tempDir := t.TempDir()
	dbPath := filepath.Join(tempDir, "stadium.sqlite3")

	store, err := NewSQLitePersistenceStore(dbPath)
	if err != nil {
		t.Fatalf("NewSQLitePersistenceStore returned error: %v", err)
	}
	defer func() {
		if closeErr := store.Close(); closeErr != nil {
			t.Fatalf("Close returned error: %v", closeErr)
		}
	}()

	ctx := context.Background()
	const (
		writers    = 8
		iterations = 40
	)

	errCh := make(chan error, writers*iterations)
	var wg sync.WaitGroup

	for w := 0; w < writers; w++ {
		workerID := w
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < iterations; i++ {
				eventID := fmt.Sprintf("evt-%d-%d", workerID, i)
				now := time.Now().UTC()

				err := store.AppendOutboxEvent(ctx, DurableOutboxEvent{
					EventID:        eventID,
					OriginServerID: "srv",
					EventType:      "match_finalized",
					PayloadJSON:    `{"match":1}`,
					CreatedAt:      now,
					NextAttemptAt:  now,
					PublishStatus:  "pending",
				})
				if err != nil {
					errCh <- fmt.Errorf("append outbox failed: %w", err)
					return
				}

				if i%2 == 0 {
					err = store.MarkOutboxEventPublished(ctx, eventID, time.Now().UTC())
				} else {
					err = store.MarkOutboxEventFailed(ctx, eventID, time.Now().UTC().Add(time.Second), "retry")
				}
				if err != nil {
					errCh <- fmt.Errorf("update outbox status failed: %w", err)
					return
				}

				receipt := DurableInboxReceipt{
					SourceServerID: "srv",
					SourceEventID:  eventID,
					ProcessedAt:    time.Now().UTC(),
				}
				if err := store.RecordInboxReceipt(ctx, receipt); err != nil {
					errCh <- fmt.Errorf("record inbox failed: %w", err)
					return
				}

				if _, err := store.HasInboxReceipt(ctx, "srv", eventID); err != nil {
					errCh <- fmt.Errorf("has inbox failed: %w", err)
					return
				}
			}
		}()
	}

	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < writers*iterations; i++ {
			if _, err := store.ListPendingOutboxEvents(ctx, 25, time.Now().UTC().Add(time.Second)); err != nil {
				errCh <- fmt.Errorf("list pending failed: %w", err)
				return
			}
			time.Sleep(2 * time.Millisecond)
		}
	}()

	wg.Wait()
	close(errCh)

	for err := range errCh {
		if err == nil {
			continue
		}
		if strings.Contains(strings.ToLower(err.Error()), "database is locked") || strings.Contains(strings.ToLower(err.Error()), "database is busy") {
			t.Fatalf("unexpected busy/locked error under concurrency: %v", err)
		}
		t.Fatalf("unexpected error under concurrency: %v", err)
	}
}
