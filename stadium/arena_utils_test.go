package stadium

import (
	"testing"
	"time"
)

func TestArenaAudience_DeduplicatesSessions(t *testing.T) {
	s1 := &Session{SessionID: 1, BotName: "p1"}
	s2 := &Session{SessionID: 2, BotName: "p2"}
	s3 := &Session{SessionID: 3, BotName: "obs"}

	arena := &Arena{
		Player1:   s1,
		Player2:   s2,
		Observers: []*Session{s3, s1, s2, s3},
	}

	audience := arena.Audience()
	if len(audience) != 3 {
		t.Fatalf("Audience length = %d, want 3", len(audience))
	}
	if audience[0].SessionID != 1 || audience[1].SessionID != 2 || audience[2].SessionID != 3 {
		t.Fatalf("unexpected audience ordering/content: %+v", audience)
	}
}

func TestArenaMoveLimitHelpers(t *testing.T) {
	arena := &Arena{
		TimeLimit:       1000 * time.Millisecond,
		Player1Handicap: 20,
		Player2Handicap: -20,
	}

	if got := arena.HandicapForPlayer(1); got != 20 {
		t.Fatalf("HandicapForPlayer(1) = %d, want 20", got)
	}
	if got := arena.HandicapForPlayer(2); got != -20 {
		t.Fatalf("HandicapForPlayer(2) = %d, want -20", got)
	}
	if got := arena.HandicapForPlayer(0); got != 0 {
		t.Fatalf("HandicapForPlayer(0) = %d, want 0", got)
	}

	if got := arena.MoveLimitForPlayer(1); got != 1200*time.Millisecond {
		t.Fatalf("MoveLimitForPlayer(1) = %v, want %v", got, 1200*time.Millisecond)
	}
	if got := arena.MoveLimitForPlayer(2); got != 800*time.Millisecond {
		t.Fatalf("MoveLimitForPlayer(2) = %v, want %v", got, 800*time.Millisecond)
	}
	if got := arena.MaxMoveLimit(); got != 1200*time.Millisecond {
		t.Fatalf("MaxMoveLimit() = %v, want %v", got, 1200*time.Millisecond)
	}
}

func TestApplyHandicapPercent(t *testing.T) {
	tests := []struct {
		name     string
		base     time.Duration
		handicap int
		want     time.Duration
	}{
		{name: "zero base", base: 0, handicap: 100, want: 0},
		{name: "positive", base: 1000 * time.Millisecond, handicap: 50, want: 1500 * time.Millisecond},
		{name: "negative", base: 1000 * time.Millisecond, handicap: -50, want: 500 * time.Millisecond},
		{name: "clamped floor", base: 1000 * time.Millisecond, handicap: -95, want: 100 * time.Millisecond},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := applyHandicapPercent(tc.base, tc.handicap)
			if got != tc.want {
				t.Fatalf("applyHandicapPercent(%v,%d) = %v, want %v", tc.base, tc.handicap, got, tc.want)
			}
		})
	}
}
