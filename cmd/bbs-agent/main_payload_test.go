package main

import (
	"testing"
)

func TestShouldForwardTurn(t *testing.T) {
	tests := []struct {
		name           string
		state          map[string]interface{}
		joinedPlayerID int
		want           bool
	}{
		{name: "observer forwards", state: map[string]interface{}{"turn_player": 2}, joinedPlayerID: 0, want: true},
		{name: "explicit your_turn true", state: map[string]interface{}{"your_turn": true}, joinedPlayerID: 1, want: true},
		{name: "explicit your_turn false", state: map[string]interface{}{"your_turn": false}, joinedPlayerID: 1, want: false},
		{name: "turn player match", state: map[string]interface{}{"turn_player": 2}, joinedPlayerID: 2, want: true},
		{name: "turn player mismatch", state: map[string]interface{}{"turn_player": 2}, joinedPlayerID: 1, want: false},
		{name: "unknown turn defaults true", state: map[string]interface{}{}, joinedPlayerID: 1, want: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := shouldForwardTurn(tc.state, tc.joinedPlayerID)
			if got != tc.want {
				t.Fatalf("shouldForwardTurn(%v, %d) = %v, want %v", tc.state, tc.joinedPlayerID, got, tc.want)
			}
		})
	}
}

func TestTerminalReward(t *testing.T) {
	tests := []struct {
		name         string
		msgType      string
		payload      interface{}
		joinedPlayer int
		status       string
		want         float64
	}{
		{name: "timeout error", msgType: "timeout", payload: nil, joinedPlayer: 1, status: "err", want: -1.0},
		{name: "ejected error", msgType: "ejected", payload: nil, joinedPlayer: 1, status: "err", want: -1.0},
		{name: "non terminal event", msgType: "data", payload: nil, joinedPlayer: 1, status: "ok", want: 0.0},
		{name: "explicit reward", msgType: "episode_end", payload: map[string]interface{}{"reward": 0.75}, joinedPlayer: 1, status: "ok", want: 0.75},
		{name: "draw gameover", msgType: "gameover", payload: map[string]interface{}{"is_draw": true}, joinedPlayer: 1, status: "ok", want: 0.0},
		{name: "winner", msgType: "gameover", payload: map[string]interface{}{"winner_player_id": 2}, joinedPlayer: 2, status: "ok", want: 1.0},
		{name: "loser", msgType: "gameover", payload: map[string]interface{}{"winner_player_id": 2}, joinedPlayer: 1, status: "ok", want: -1.0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := terminalReward(tc.msgType, tc.payload, tc.joinedPlayer, tc.status)
			if got != tc.want {
				t.Fatalf("terminalReward(%q, payload, %d, %q) = %v, want %v", tc.msgType, tc.joinedPlayer, tc.status, got, tc.want)
			}
		})
	}
}

func TestBuildStatePayload_FromJSONString(t *testing.T) {
	raw := `{"turn_player":2,"reward":1.5,"done":true}`
	payload := buildStatePayload(raw, 2)

	if payload["source"] != "server_data" {
		t.Fatalf("source = %#v, want %q", payload["source"], "server_data")
	}
	if payload["raw_state"] != raw {
		t.Fatalf("raw_state = %#v, want %q", payload["raw_state"], raw)
	}
	if payload["turn_player"] != 2 {
		t.Fatalf("turn_player = %#v, want %d", payload["turn_player"], 2)
	}
	if payload["your_turn"] != true {
		t.Fatalf("your_turn = %#v, want %v", payload["your_turn"], true)
	}
	if _, ok := payload["state_obj"].(map[string]interface{}); !ok {
		t.Fatalf("state_obj missing or wrong type: %#v", payload["state_obj"])
	}
}

func TestBuildStatePayload_FromMapAndFallbackTurnField(t *testing.T) {
	raw := map[string]interface{}{"turn": 1, "reward": 0.25}
	payload := buildStatePayload(raw, 1)

	if payload["turn_player"] != 1 {
		t.Fatalf("turn_player = %#v, want %d", payload["turn_player"], 1)
	}
	if payload["your_turn"] != true {
		t.Fatalf("your_turn = %#v, want %v", payload["your_turn"], true)
	}
}

func TestStatePayloadDone(t *testing.T) {
	tests := []struct {
		name  string
		state map[string]interface{}
		want  bool
	}{
		{name: "nil", state: nil, want: false},
		{name: "top level true", state: map[string]interface{}{"done": true}, want: true},
		{name: "state object true", state: map[string]interface{}{"state_obj": map[string]interface{}{"done": true}}, want: true},
		{name: "missing", state: map[string]interface{}{}, want: false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := statePayloadDone(tc.state)
			if got != tc.want {
				t.Fatalf("statePayloadDone(%v) = %v, want %v", tc.state, got, tc.want)
			}
		})
	}
}

func TestStatePayloadReward(t *testing.T) {
	tests := []struct {
		name  string
		state map[string]interface{}
		want  float64
	}{
		{name: "nil", state: nil, want: 0},
		{name: "top level reward", state: map[string]interface{}{"reward": 2}, want: 2},
		{name: "state object reward", state: map[string]interface{}{"state_obj": map[string]interface{}{"reward": 0.5}}, want: 0.5},
		{name: "missing", state: map[string]interface{}{}, want: 0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := statePayloadReward(tc.state)
			if got != tc.want {
				t.Fatalf("statePayloadReward(%v) = %v, want %v", tc.state, got, tc.want)
			}
		})
	}
}
