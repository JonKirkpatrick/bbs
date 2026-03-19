package main

import (
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

const (
	minStreamFlushInterval = 16 * time.Millisecond
	maxStreamFlushInterval = 2 * time.Second
)

type wsEventEnvelope struct {
	Stream  string      `json:"stream"`
	Type    string      `json:"type"`
	Payload interface{} `json:"payload,omitempty"`
	SentAt  string      `json:"sent_at"`
}

func wsCheckOriginSameHost(r *http.Request) bool {
	origin := strings.TrimSpace(r.Header.Get("Origin"))
	if origin == "" {
		return true
	}

	parsed, err := url.Parse(origin)
	if err != nil {
		return false
	}

	return requestHostOnly(parsed.Host) == requestHostOnly(r.Host)
}

func resolveStreamFlushInterval(r *http.Request, fallback time.Duration) time.Duration {
	fallback = clampStreamFlushInterval(fallback)

	if raw := strings.TrimSpace(r.URL.Query().Get("stream_interval_ms")); raw != "" {
		if ms, err := strconv.Atoi(raw); err == nil && ms > 0 {
			return clampStreamFlushInterval(time.Duration(ms) * time.Millisecond)
		}
	}

	if raw := strings.TrimSpace(r.URL.Query().Get("max_hz")); raw != "" {
		if hz, err := strconv.ParseFloat(raw, 64); err == nil && hz > 0 {
			interval := time.Duration(float64(time.Second) / hz)
			if interval <= 0 {
				interval = minStreamFlushInterval
			}
			return clampStreamFlushInterval(interval)
		}
	}

	return fallback
}

func clampStreamFlushInterval(v time.Duration) time.Duration {
	if v < minStreamFlushInterval {
		return minStreamFlushInterval
	}
	if v > maxStreamFlushInterval {
		return maxStreamFlushInterval
	}
	return v
}

func writeStreamWSEvent(conn *websocket.Conn, streamName, eventType string, payload interface{}) error {
	if err := conn.SetWriteDeadline(time.Now().Add(5 * time.Second)); err != nil {
		return err
	}

	return conn.WriteJSON(wsEventEnvelope{
		Stream:  streamName,
		Type:    eventType,
		Payload: payload,
		SentAt:  time.Now().UTC().Format(time.RFC3339Nano),
	})
}
