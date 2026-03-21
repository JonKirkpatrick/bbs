package stadium

import (
	"testing"
	"time"
)

func TestSubscribeUnsubscribe(t *testing.T) {
	m := newTestManager()
	ch := m.Subscribe()

	m.mu.Lock()
	_, exists := m.subscribers[ch]
	m.mu.Unlock()
	if !exists {
		t.Fatalf("subscriber channel was not registered")
	}

	m.Unsubscribe(ch)

	m.mu.Lock()
	_, exists = m.subscribers[ch]
	m.mu.Unlock()
	if exists {
		t.Fatalf("subscriber channel was not removed")
	}
}

func TestPublishArenaList_EmitsExpectedEvents(t *testing.T) {
	m := newTestManager()
	ch := m.Subscribe()

	m.PublishArenaList()

	select {
	case ev := <-ch:
		if ev.Type != "arena_list" {
			t.Fatalf("first event type = %q, want %q", ev.Type, "arena_list")
		}
	case <-time.After(200 * time.Millisecond):
		t.Fatal("timed out waiting for first published event")
	}

	select {
	case ev := <-ch:
		if ev.Type != "manager_state" {
			t.Fatalf("second event type = %q, want %q", ev.Type, "manager_state")
		}
	case <-time.After(200 * time.Millisecond):
		t.Fatal("timed out waiting for second published event")
	}
}

func TestPublishEvents_NonBlockingWithSlowSubscriber(t *testing.T) {
	m := newTestManager()
	slow := make(chan StadiumEvent)
	done := make(chan struct{})

	go func() {
		m.publishEvents([]chan StadiumEvent{slow}, []StadiumEvent{{Type: "arena_list", Payload: nil}})
		close(done)
	}()

	select {
	case <-done:
		// expected: send is dropped via default branch and does not block
	case <-time.After(200 * time.Millisecond):
		t.Fatal("publishEvents blocked on slow subscriber")
	}
}
