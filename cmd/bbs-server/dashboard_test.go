package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/JonKirkpatrick/bbs/stadium"
)

func TestParseGameArgs(t *testing.T) {
	tests := []struct {
		name string
		raw  string
		want []string
	}{
		{name: "empty", raw: "", want: nil},
		{name: "csv", raw: "a=1, b=2 , c=3", want: []string{"a=1", "b=2", "c=3"}},
		{name: "whitespace fields", raw: "alpha beta   gamma", want: []string{"alpha", "beta", "gamma"}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := parseGameArgs(tc.raw)
			if len(got) != len(tc.want) {
				t.Fatalf("parseGameArgs(%q) length = %d, want %d (%v)", tc.raw, len(got), len(tc.want), got)
			}
			for i := range tc.want {
				if got[i] != tc.want[i] {
					t.Fatalf("parseGameArgs(%q)[%d] = %q, want %q", tc.raw, i, got[i], tc.want[i])
				}
			}
		})
	}
}

func TestDashboardSSEQuery(t *testing.T) {
	if got := dashboardSSEQuery("", ""); got != "" {
		t.Fatalf("dashboardSSEQuery empty = %q, want empty", got)
	}

	got := dashboardSSEQuery("admin123", "owner_abc")
	want := "?admin_key=admin123&owner_token=owner_abc"
	if got != want {
		t.Fatalf("dashboardSSEQuery = %q, want %q", got, want)
	}
}

func TestRequestHostOnly(t *testing.T) {
	tests := []struct {
		name string
		raw  string
		want string
	}{
		{name: "empty", raw: "", want: "localhost"},
		{name: "host only", raw: "example.com", want: "example.com"},
		{name: "host and port", raw: "example.com:3000", want: "example.com"},
		{name: "ipv6 and port", raw: "[::1]:3000", want: "::1"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := requestHostOnly(tc.raw); got != tc.want {
				t.Fatalf("requestHostOnly(%q) = %q, want %q", tc.raw, got, tc.want)
			}
		})
	}
}

func TestDashboardBotEndpoint(t *testing.T) {
	botServerPort = "8080"
	r := httptest.NewRequest(http.MethodGet, "http://example.com/dashboard", nil)
	r.Host = "example.com:3000"

	if got := dashboardBotHost(r); got != "example.com" {
		t.Fatalf("dashboardBotHost = %q, want %q", got, "example.com")
	}
	if got := dashboardBotEndpoint(r); got != "example.com:8080" {
		t.Fatalf("dashboardBotEndpoint = %q, want %q", got, "example.com:8080")
	}
}

func TestRequireAdmin(t *testing.T) {
	t.Setenv(dashboardAdminKeyEnv, "secret")

	t.Run("unauthorized", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodPost, "/admin/create-arena", nil)
		rr := httptest.NewRecorder()

		key, ok := requireAdmin(rr, req)
		if ok || key != "" {
			t.Fatalf("requireAdmin should fail without key, got ok=%v key=%q", ok, key)
		}
		if !strings.Contains(rr.Body.String(), "Admin authorization failed") {
			t.Fatalf("expected auth failure message, got: %s", rr.Body.String())
		}
	})

	t.Run("authorized query", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodPost, "/admin/create-arena?admin_key=secret", nil)
		rr := httptest.NewRecorder()

		key, ok := requireAdmin(rr, req)
		if !ok || key != "secret" {
			t.Fatalf("requireAdmin should pass, got ok=%v key=%q", ok, key)
		}
	})
}

func TestAdminDebugServerIdentity(t *testing.T) {
	t.Setenv(dashboardAdminKeyEnv, "secret")

	store := stadium.NewInMemoryPersistenceStore()
	stadium.DefaultManager.SetPersistenceStore(store)
	_, err := stadium.DefaultManager.BootstrapServerIdentity(context.Background(), "debug-node", "v0.0.0")
	if err != nil {
		t.Fatalf("BootstrapServerIdentity returned error: %v", err)
	}

	req := httptest.NewRequest(http.MethodGet, "/admin/debug/server-identity?admin_key=secret", nil)
	rr := httptest.NewRecorder()
	handleAdminDebugServerIdentity(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rr.Code)
	}
	var payload map[string]interface{}
	if err := json.Unmarshal(rr.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid json response: %v", err)
	}
	if payload["status"] != "ok" {
		t.Fatalf("expected status ok, got %v", payload["status"])
	}
}

func TestAdminDebugOutboxAndRecentMatches(t *testing.T) {
	t.Setenv(dashboardAdminKeyEnv, "secret")

	store := stadium.NewInMemoryPersistenceStore()
	stadium.DefaultManager.SetPersistenceStore(store)

	err := store.AppendOutboxEvent(context.Background(), stadium.DurableOutboxEvent{
		EventID:       "evt_99",
		EventType:     "match_finalized",
		PayloadJSON:   `{"match_id":99}`,
		CreatedAt:     time.Now().UTC(),
		NextAttemptAt: time.Now().UTC().Add(-time.Second),
		PublishStatus: "pending",
	})
	if err != nil {
		t.Fatalf("AppendOutboxEvent returned error: %v", err)
	}

	err = store.AppendMatch(context.Background(), stadium.DurableMatch{
		MatchID:        99,
		ArenaID:        99,
		Game:           "counter",
		TerminalStatus: "completed",
		EndReason:      "done",
		StartedAt:      time.Now().UTC().Add(-time.Minute),
		EndedAt:        time.Now().UTC(),
	}, []stadium.DurableMatchMove{{
		MatchID:    99,
		Sequence:   1,
		BotID:      "bot_demo",
		Move:       "x",
		OccurredAt: time.Now().UTC(),
	}})
	if err != nil {
		t.Fatalf("AppendMatch returned error: %v", err)
	}

	outboxReq := httptest.NewRequest(http.MethodGet, "/admin/debug/outbox?admin_key=secret&limit=5", nil)
	outboxRR := httptest.NewRecorder()
	handleAdminDebugOutbox(outboxRR, outboxReq)
	if outboxRR.Code != http.StatusOK {
		t.Fatalf("outbox endpoint expected 200, got %d", outboxRR.Code)
	}

	recentReq := httptest.NewRequest(http.MethodGet, "/admin/debug/recent-matches?admin_key=secret&bot_id=bot_demo&limit=5", nil)
	recentRR := httptest.NewRecorder()
	handleAdminDebugRecentMatches(recentRR, recentReq)
	if recentRR.Code != http.StatusOK {
		t.Fatalf("recent matches endpoint expected 200, got %d", recentRR.Code)
	}
}

func TestAPIStatus(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/api/status", nil)
	rr := httptest.NewRecorder()

	handleAPIStatus(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rr.Code)
	}

	var payload map[string]interface{}
	if err := json.Unmarshal(rr.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid json response: %v", err)
	}

	if payload["status"] != "ok" {
		t.Fatalf("expected status=ok, got %v", payload["status"])
	}
}

func TestAPIGameCatalog(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/api/game-catalog", nil)
	rr := httptest.NewRecorder()

	handleAPIGameCatalog(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rr.Code)
	}

	var payload []map[string]interface{}
	if err := json.Unmarshal(rr.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid json response: %v", err)
	}
}

func TestAPIArenas(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/api/arenas", nil)
	req.Host = "localhost:3000"
	rr := httptest.NewRecorder()

	handleAPIArenas(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rr.Code)
	}

	var payload map[string]interface{}
	if err := json.Unmarshal(rr.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid json response: %v", err)
	}

	if payload["status"] != "ok" {
		t.Fatalf("expected status=ok, got %v", payload["status"])
	}

	if _, ok := payload["arenas"]; !ok {
		t.Fatalf("expected arenas field in response")
	}
}

func TestAPIArenas_ViewerURLUsesCanvasRoute(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/api/arenas", nil)
	req.Host = "localhost:3000"
	rr := httptest.NewRecorder()

	handleAPIArenas(rr, req)

	if rr.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rr.Code)
	}

	var payload struct {
		Status string `json:"status"`
		Arenas []struct {
			ViewerURL string `json:"viewer_url"`
		} `json:"arenas"`
	}

	if err := json.Unmarshal(rr.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid json response: %v", err)
	}

	if payload.Status != "ok" {
		t.Fatalf("expected status=ok, got %q", payload.Status)
	}

	if len(payload.Arenas) == 0 {
		t.Skip("no active arenas available in test environment")
	}

	for _, arena := range payload.Arenas {
		if !strings.Contains(arena.ViewerURL, "/viewer/canvas?") {
			t.Fatalf("expected viewer_url to contain /viewer/canvas, got %q", arena.ViewerURL)
		}
	}
}

func TestMockFederationIngest_DisabledByDefault(t *testing.T) {
	req := httptest.NewRequest(http.MethodPost, "/federation/mock/ingest", strings.NewReader(`{"event_id":"evt_1","origin_server_id":"srv_1"}`))
	rr := httptest.NewRecorder()
	handleMockFederationIngest(rr, req)
	if rr.Code != http.StatusNotFound {
		t.Fatalf("expected 404 when disabled, got %d", rr.Code)
	}
}

func TestMockFederationIngest_AcceptsAndDedupes(t *testing.T) {
	t.Setenv(mockFederationReceiverEnabledEnv, "true")
	store := stadium.NewInMemoryPersistenceStore()
	stadium.DefaultManager.SetPersistenceStore(store)

	body := `{"event_id":"evt_10","origin_server_id":"srv_remote","event_type":"match_finalized"}`
	req := httptest.NewRequest(http.MethodPost, "/federation/mock/ingest", strings.NewReader(body))
	rr := httptest.NewRecorder()
	handleMockFederationIngest(rr, req)
	if rr.Code != http.StatusAccepted {
		t.Fatalf("expected 202, got %d", rr.Code)
	}

	dupReq := httptest.NewRequest(http.MethodPost, "/federation/mock/ingest", strings.NewReader(body))
	dupRR := httptest.NewRecorder()
	handleMockFederationIngest(dupRR, dupReq)
	if dupRR.Code != http.StatusAccepted {
		t.Fatalf("expected 202 duplicate ack, got %d", dupRR.Code)
	}

	var payload map[string]interface{}
	if err := json.Unmarshal(dupRR.Body.Bytes(), &payload); err != nil {
		t.Fatalf("invalid duplicate ack json: %v", err)
	}
	if payload["duplicate"] != true {
		t.Fatalf("expected duplicate=true, got %v", payload["duplicate"])
	}
}

func TestMockFederationIngest_TokenRequired(t *testing.T) {
	t.Setenv(mockFederationReceiverEnabledEnv, "true")
	t.Setenv(mockFederationReceiverTokenEnv, "secret-token")

	unauthReq := httptest.NewRequest(http.MethodPost, "/federation/mock/ingest", strings.NewReader(`{"event_id":"evt_20","origin_server_id":"srv_remote"}`))
	unauthRR := httptest.NewRecorder()
	handleMockFederationIngest(unauthRR, unauthReq)
	if unauthRR.Code != http.StatusUnauthorized {
		t.Fatalf("expected 401 without token, got %d", unauthRR.Code)
	}

	authReq := httptest.NewRequest(http.MethodPost, "/federation/mock/ingest", strings.NewReader(`{"event_id":"evt_20","origin_server_id":"srv_remote"}`))
	authReq.Header.Set("Authorization", "Bearer secret-token")
	authRR := httptest.NewRecorder()
	handleMockFederationIngest(authRR, authReq)
	if authRR.Code != http.StatusAccepted {
		t.Fatalf("expected 202 with valid token, got %d", authRR.Code)
	}
}
