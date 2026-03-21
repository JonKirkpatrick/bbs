package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestWSCheckOriginSameHost(t *testing.T) {
	tests := []struct {
		name   string
		host   string
		origin string
		want   bool
	}{
		{name: "no origin allowed", host: "example.com:3000", origin: "", want: true},
		{name: "same host allowed", host: "example.com:3000", origin: "http://example.com:8080", want: true},
		{name: "different host denied", host: "example.com:3000", origin: "http://evil.com:8080", want: false},
		{name: "invalid origin denied", host: "example.com:3000", origin: "://bad", want: false},
		{name: "same ipv6 allowed", host: "[::1]:3000", origin: "http://[::1]:8080", want: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodGet, "http://"+tc.host+"/dashboard-ws", nil)
			r.Host = tc.host
			if tc.origin != "" {
				r.Header.Set("Origin", tc.origin)
			}
			got := wsCheckOriginSameHost(r)
			if got != tc.want {
				t.Fatalf("wsCheckOriginSameHost(host=%q, origin=%q) = %v, want %v", tc.host, tc.origin, got, tc.want)
			}
		})
	}
}

func TestClampStreamFlushInterval(t *testing.T) {
	tests := []struct {
		name string
		in   time.Duration
		want time.Duration
	}{
		{name: "below min", in: 1 * time.Millisecond, want: minStreamFlushInterval},
		{name: "at min", in: minStreamFlushInterval, want: minStreamFlushInterval},
		{name: "middle", in: 250 * time.Millisecond, want: 250 * time.Millisecond},
		{name: "above max", in: 5 * time.Second, want: maxStreamFlushInterval},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := clampStreamFlushInterval(tc.in)
			if got != tc.want {
				t.Fatalf("clampStreamFlushInterval(%v) = %v, want %v", tc.in, got, tc.want)
			}
		})
	}
}

func TestResolveStreamFlushInterval(t *testing.T) {
	fallback := 150 * time.Millisecond

	t.Run("uses fallback when no query", func(t *testing.T) {
		r := httptest.NewRequest(http.MethodGet, "/dashboard-sse", nil)
		got := resolveStreamFlushInterval(r, fallback)
		if got != fallback {
			t.Fatalf("resolveStreamFlushInterval fallback = %v, want %v", got, fallback)
		}
	})

	t.Run("uses stream_interval_ms", func(t *testing.T) {
		r := httptest.NewRequest(http.MethodGet, "/dashboard-sse?stream_interval_ms=250", nil)
		got := resolveStreamFlushInterval(r, fallback)
		if got != 250*time.Millisecond {
			t.Fatalf("resolveStreamFlushInterval stream_interval_ms = %v, want %v", got, 250*time.Millisecond)
		}
	})

	t.Run("stream_interval_ms clamped", func(t *testing.T) {
		r := httptest.NewRequest(http.MethodGet, "/dashboard-sse?stream_interval_ms=1", nil)
		got := resolveStreamFlushInterval(r, fallback)
		if got != minStreamFlushInterval {
			t.Fatalf("resolveStreamFlushInterval clamped = %v, want %v", got, minStreamFlushInterval)
		}
	})

	t.Run("uses max_hz", func(t *testing.T) {
		r := httptest.NewRequest(http.MethodGet, "/dashboard-sse?max_hz=20", nil)
		got := resolveStreamFlushInterval(r, fallback)
		if got != 50*time.Millisecond {
			t.Fatalf("resolveStreamFlushInterval max_hz = %v, want %v", got, 50*time.Millisecond)
		}
	})

	t.Run("invalid query falls back", func(t *testing.T) {
		r := httptest.NewRequest(http.MethodGet, "/dashboard-sse?stream_interval_ms=abc&max_hz=nope", nil)
		got := resolveStreamFlushInterval(r, fallback)
		if got != fallback {
			t.Fatalf("resolveStreamFlushInterval invalid query = %v, want %v", got, fallback)
		}
	})
}
