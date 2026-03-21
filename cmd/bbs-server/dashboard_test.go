package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
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
