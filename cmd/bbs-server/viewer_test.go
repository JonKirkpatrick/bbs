package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestParsePositiveQueryInt(t *testing.T) {
	tests := []struct {
		name string
		url  string
		key  string
		want int
		ok   bool
	}{
		{name: "valid", url: "/viewer?arena_id=42", key: "arena_id", want: 42, ok: true},
		{name: "missing", url: "/viewer", key: "arena_id", want: 0, ok: false},
		{name: "zero", url: "/viewer?arena_id=0", key: "arena_id", want: 0, ok: false},
		{name: "negative", url: "/viewer?arena_id=-1", key: "arena_id", want: 0, ok: false},
		{name: "not number", url: "/viewer?arena_id=x", key: "arena_id", want: 0, ok: false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodGet, tc.url, nil)
			got, ok := parsePositiveQueryInt(r, tc.key)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("parsePositiveQueryInt(%q,%q) = (%d,%v), want (%d,%v)", tc.url, tc.key, got, ok, tc.want, tc.ok)
			}
		})
	}
}

func TestDownsampleReplayRawFrames(t *testing.T) {
	frames := make([]rawViewerFrame, 0, 10)
	for i := 0; i < 10; i++ {
		frames = append(frames, rawViewerFrame{MoveIndex: i, RawState: "s"})
	}

	t.Run("no downsample", func(t *testing.T) {
		got := downsampleReplayRawFrames(frames, 20)
		if len(got) != 10 {
			t.Fatalf("len(got) = %d, want 10", len(got))
		}
	})

	t.Run("single frame", func(t *testing.T) {
		got := downsampleReplayRawFrames(frames, 1)
		if len(got) != 1 {
			t.Fatalf("len(got) = %d, want 1", len(got))
		}
		if got[0].MoveIndex != 9 {
			t.Fatalf("single frame should be last, got MoveIndex=%d", got[0].MoveIndex)
		}
	})

	t.Run("downsample preserves endpoints", func(t *testing.T) {
		got := downsampleReplayRawFrames(frames, 4)
		if len(got) != 4 {
			t.Fatalf("len(got) = %d, want 4", len(got))
		}
		if got[0].MoveIndex != 0 {
			t.Fatalf("first frame MoveIndex=%d, want 0", got[0].MoveIndex)
		}
		if got[len(got)-1].MoveIndex != 9 {
			t.Fatalf("last frame MoveIndex=%d, want 9", got[len(got)-1].MoveIndex)
		}
	})
}
