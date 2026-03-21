package main

import (
	"strings"
	"testing"
	"time"
)

type testGameDefaultPolicies struct{}

func (testGameDefaultPolicies) GetName() string                              { return "test" }
func (testGameDefaultPolicies) GetState() string                             { return "{}" }
func (testGameDefaultPolicies) ValidateMove(playerID int, move string) error { return nil }
func (testGameDefaultPolicies) ApplyMove(playerID int, move string) error    { return nil }
func (testGameDefaultPolicies) IsGameOver() (bool, string)                   { return false, "" }

type testGameNoMoveClock struct{ testGameDefaultPolicies }

func (testGameNoMoveClock) EnforceMoveClock() bool { return false }

type testGameNoHandicap struct{ testGameDefaultPolicies }

func (testGameNoHandicap) SupportsHandicap() bool { return false }

func TestIsStrictBoolToken(t *testing.T) {
	tests := []struct {
		name  string
		input string
		want  bool
	}{
		{name: "true", input: "true", want: true},
		{name: "false", input: "false", want: true},
		{name: "trimmed mixed case", input: "  TrUe  ", want: true},
		{name: "reject one", input: "1", want: false},
		{name: "reject yes", input: "yes", want: false},
		{name: "reject empty", input: "", want: false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := isStrictBoolToken(tc.input)
			if got != tc.want {
				t.Fatalf("isStrictBoolToken(%q) = %v, want %v", tc.input, got, tc.want)
			}
		})
	}
}

func TestNormalizeCreateGameArgs(t *testing.T) {
	in := []string{"  a=1  ", "", "   ", "b=2", " c=3 "}
	want := []string{"a=1", "b=2", "c=3"}

	got := normalizeCreateGameArgs(in)
	if len(got) != len(want) {
		t.Fatalf("normalizeCreateGameArgs length = %d, want %d (%v)", len(got), len(want), got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("normalizeCreateGameArgs[%d] = %q, want %q", i, got[i], want[i])
		}
	}
}

func TestResolveArenaRuntimeOptions_Defaults(t *testing.T) {
	game := testGameDefaultPolicies{}

	timeLimit, allowHandicap, err := resolveArenaRuntimeOptions(game, "", "")
	if err != nil {
		t.Fatalf("resolveArenaRuntimeOptions returned unexpected error: %v", err)
	}
	if timeLimit != time.Duration(defaultCreateTimeLimitMS)*time.Millisecond {
		t.Fatalf("timeLimit = %v, want %v", timeLimit, time.Duration(defaultCreateTimeLimitMS)*time.Millisecond)
	}
	if allowHandicap {
		t.Fatalf("allowHandicap = true, want false")
	}
}

func TestResolveArenaRuntimeOptions_ExplicitValues(t *testing.T) {
	game := testGameDefaultPolicies{}

	timeLimit, allowHandicap, err := resolveArenaRuntimeOptions(game, "2500", "true")
	if err != nil {
		t.Fatalf("resolveArenaRuntimeOptions returned unexpected error: %v", err)
	}
	if timeLimit != 2500*time.Millisecond {
		t.Fatalf("timeLimit = %v, want %v", timeLimit, 2500*time.Millisecond)
	}
	if !allowHandicap {
		t.Fatalf("allowHandicap = false, want true")
	}
}

func TestResolveArenaRuntimeOptions_InvalidTime(t *testing.T) {
	game := testGameDefaultPolicies{}

	_, _, err := resolveArenaRuntimeOptions(game, "0", "false")
	if err == nil {
		t.Fatal("expected error for non-positive time")
	}
	if !strings.Contains(err.Error(), "time limit must be a positive integer") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestResolveArenaRuntimeOptions_InvalidAllowHandicap(t *testing.T) {
	game := testGameDefaultPolicies{}

	_, _, err := resolveArenaRuntimeOptions(game, "1000", "maybe")
	if err == nil {
		t.Fatal("expected error for invalid allow_handicap token")
	}
	if !strings.Contains(err.Error(), "allow_handicap must be true or false") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestResolveArenaRuntimeOptions_DisablesHandicapForGamePolicies(t *testing.T) {
	t.Run("move clock disabled", func(t *testing.T) {
		game := testGameNoMoveClock{}
		_, allowHandicap, err := resolveArenaRuntimeOptions(game, "1200", "true")
		if err != nil {
			t.Fatalf("resolveArenaRuntimeOptions returned unexpected error: %v", err)
		}
		if allowHandicap {
			t.Fatalf("allowHandicap = true, want false when move clock is disabled")
		}
	})

	t.Run("handicap unsupported", func(t *testing.T) {
		game := testGameNoHandicap{}
		_, allowHandicap, err := resolveArenaRuntimeOptions(game, "1200", "true")
		if err != nil {
			t.Fatalf("resolveArenaRuntimeOptions returned unexpected error: %v", err)
		}
		if allowHandicap {
			t.Fatalf("allowHandicap = true, want false when handicap is unsupported")
		}
	})
}

func TestParseCreateCommand_UsageErrors(t *testing.T) {
	tests := []struct {
		name  string
		parts []string
	}{
		{name: "missing type", parts: []string{"CREATE"}},
		{name: "blank type", parts: []string{"CREATE", "   "}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, _, _, _, err := parseCreateCommand(tc.parts)
			if err == nil {
				t.Fatalf("parseCreateCommand(%v) expected usage error", tc.parts)
			}
			if !strings.Contains(err.Error(), "Usage: CREATE") {
				t.Fatalf("unexpected error for %v: %v", tc.parts, err)
			}
		})
	}
}

func TestParseCreateCommand_UnknownGame(t *testing.T) {
	_, _, _, _, err := parseCreateCommand([]string{"CREATE", "definitely_not_a_real_game"})
	if err == nil {
		t.Fatal("expected error for unknown game")
	}
	if !strings.Contains(strings.ToLower(err.Error()), "not found") {
		t.Fatalf("unexpected error: %v", err)
	}
}
