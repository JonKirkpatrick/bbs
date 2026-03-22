package stadium

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestHTTPOutboxPublisher_PublishSuccessWithAck(t *testing.T) {
	var sawAuth string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		sawAuth = r.Header.Get("Authorization")
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"status":"ok","ack_id":"ack-1"}`))
	}))
	defer srv.Close()

	publisher, err := NewHTTPOutboxPublisher(srv.URL, "token-123", time.Second)
	if err != nil {
		t.Fatalf("NewHTTPOutboxPublisher returned error: %v", err)
	}

	err = publisher.PublishOutboxEvent(context.Background(), DurableOutboxEvent{
		EventID:       "evt_1",
		EventType:     "match_finalized",
		PayloadJSON:   `{"match_id":1}`,
		CreatedAt:     time.Now().UTC(),
		PublishStatus: "pending",
	})
	if err != nil {
		t.Fatalf("PublishOutboxEvent returned error: %v", err)
	}
	if sawAuth != "Bearer token-123" {
		t.Fatalf("expected bearer auth header, got %q", sawAuth)
	}
}

func TestHTTPOutboxPublisher_PublishRejected(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusAccepted)
		_, _ = w.Write([]byte(`{"status":"rejected","error":"duplicate event"}`))
	}))
	defer srv.Close()

	publisher, err := NewHTTPOutboxPublisher(srv.URL, "", time.Second)
	if err != nil {
		t.Fatalf("NewHTTPOutboxPublisher returned error: %v", err)
	}

	err = publisher.PublishOutboxEvent(context.Background(), DurableOutboxEvent{
		EventID:     "evt_1",
		EventType:   "match_finalized",
		CreatedAt:   time.Now().UTC(),
		PayloadJSON: `{"match_id":1}`,
	})
	if err == nil {
		t.Fatal("expected rejection error")
	}
	if got := err.Error(); got != "duplicate event" {
		t.Fatalf("unexpected error message: %q", got)
	}
}

func TestHTTPOutboxPublisher_HTTPFailure(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadGateway)
		_, _ = w.Write([]byte("upstream unavailable"))
	}))
	defer srv.Close()

	publisher, err := NewHTTPOutboxPublisher(srv.URL, "", time.Second)
	if err != nil {
		t.Fatalf("NewHTTPOutboxPublisher returned error: %v", err)
	}

	err = publisher.PublishOutboxEvent(context.Background(), DurableOutboxEvent{
		EventID:     "evt_2",
		EventType:   "match_finalized",
		CreatedAt:   time.Now().UTC(),
		PayloadJSON: `{"match_id":2}`,
	})
	if err == nil {
		t.Fatal("expected HTTP failure error")
	}
	if want := "status=502"; err != nil && !strings.Contains(err.Error(), want) {
		t.Fatalf("expected error to contain %q, got %q", want, err.Error())
	}
}
