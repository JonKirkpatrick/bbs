package main

import (
	"reflect"
	"testing"

	"github.com/JonKirkpatrick/bbs/stadium"
)

func TestNormalizePort_Valid(t *testing.T) {
	tests := []struct {
		name  string
		input string
		want  string
	}{
		{name: "simple", input: "8080", want: "8080"},
		{name: "trimmed", input: " 3000 ", want: "3000"},
		{name: "min", input: "1", want: "1"},
		{name: "max", input: "65535", want: "65535"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := normalizePort(tc.input, "stadium")
			if err != nil {
				t.Fatalf("normalizePort(%q) returned unexpected error: %v", tc.input, err)
			}
			if got != tc.want {
				t.Fatalf("normalizePort(%q) = %q, want %q", tc.input, got, tc.want)
			}
		})
	}
}

func TestNormalizePort_Invalid(t *testing.T) {
	tests := []struct {
		name  string
		input string
	}{
		{name: "empty", input: ""},
		{name: "alpha", input: "abc"},
		{name: "zero", input: "0"},
		{name: "negative", input: "-1"},
		{name: "too high", input: "70000"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, err := normalizePort(tc.input, "dash")
			if err == nil {
				t.Fatalf("normalizePort(%q) expected error", tc.input)
			}
		})
	}
}

func TestParseRegisterOptions_EmptyAndCSV(t *testing.T) {
	tests := []struct {
		name         string
		raw          []string
		wantCaps     []string
		wantOwnerTok string
	}{
		{
			name:         "empty yields nil caps",
			raw:          []string{"", "   "},
			wantCaps:     nil,
			wantOwnerTok: "",
		},
		{
			name:         "csv and tokens",
			raw:          []string{"any,grid", " rl ", "owner_token=owner_123"},
			wantCaps:     []string{"any", "grid", "rl"},
			wantOwnerTok: "owner_123",
		},
		{
			name:         "owner token case-insensitive prefix",
			raw:          []string{"OWNER_TOKEN=owner_xyz", "a"},
			wantCaps:     []string{"a"},
			wantOwnerTok: "owner_xyz",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			caps, owner := parseRegisterOptions(tc.raw)
			if !reflect.DeepEqual(caps, tc.wantCaps) {
				t.Fatalf("caps = %#v, want %#v", caps, tc.wantCaps)
			}
			if owner != tc.wantOwnerTok {
				t.Fatalf("owner = %q, want %q", owner, tc.wantOwnerTok)
			}
		})
	}
}

func TestParseWinnerResult_PlayerDrawInvalid(t *testing.T) {
	tests := []struct {
		name       string
		input      string
		wantID     int
		wantIsDraw bool
	}{
		{name: "player1", input: "player 1", wantID: 1, wantIsDraw: false},
		{name: "player2", input: "PLAYER 2", wantID: 2, wantIsDraw: false},
		{name: "draw", input: "draw", wantID: 0, wantIsDraw: true},
		{name: "empty", input: "", wantID: 0, wantIsDraw: false},
		{name: "invalid player", input: "player 3", wantID: 0, wantIsDraw: false},
		{name: "junk", input: "winner maybe", wantID: 0, wantIsDraw: false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			gotID, gotDraw := parseWinnerResult(tc.input)
			if gotID != tc.wantID || gotDraw != tc.wantIsDraw {
				t.Fatalf("parseWinnerResult(%q) = (%d, %v), want (%d, %v)", tc.input, gotID, gotDraw, tc.wantID, tc.wantIsDraw)
			}
		})
	}
}

func TestCompactGameoverPayload_FieldsPresent(t *testing.T) {
	record := stadium.MatchRecord{
		MatchID:        10,
		ArenaID:        7,
		Game:           "counter",
		TerminalStatus: "completed",
		EndReason:      "normal",
		WinnerPlayerID: 1,
		WinnerBotID:    "bot_abc",
		WinnerBotName:  "alpha",
		IsDraw:         false,
		MoveCount:      12,
		StartedAt:      "2026-03-21T12:00:00Z",
		EndedAt:        "2026-03-21T12:01:00Z",
	}

	payload := compactGameoverPayload(record)

	wantKeys := []string{
		"match_id", "arena_id", "game", "terminal_status", "end_reason",
		"winner_player_id", "winner_bot_id", "winner_bot_name", "is_draw",
		"move_count", "started_at", "ended_at",
	}

	for _, key := range wantKeys {
		if _, ok := payload[key]; !ok {
			t.Fatalf("payload missing key %q", key)
		}
	}

	if got := payload["winner_bot_id"]; got != "bot_abc" {
		t.Fatalf("winner_bot_id = %#v, want %q", got, "bot_abc")
	}
	if got := payload["move_count"]; got != 12 {
		t.Fatalf("move_count = %#v, want %d", got, 12)
	}
}
