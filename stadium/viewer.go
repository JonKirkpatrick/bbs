package stadium

import (
	"time"
)

// ArenaViewerPlayer holds the viewer-relevant details for one arena participant.
type ArenaViewerPlayer struct {
	Name   string
	BotID  string
	Wins   int
	Losses int
	Draws  int
}

// ArenaViewerState is a lock-safe snapshot used by live viewer endpoints.
type ArenaViewerState struct {
	ArenaID        int
	Game           string
	GameState      string
	MoveCount      int
	Status         string
	LastMoveAt     string
	WinnerPlayerID int
	IsDraw         bool
	Player1        ArenaViewerPlayer
	Player2        ArenaViewerPlayer
}

// GetArenaViewerState returns the current renderable state for one arena.
func (m *Manager) GetArenaViewerState(arenaID int) (ArenaViewerState, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()

	arena, exists := m.Arenas[arenaID]
	if !exists || arena == nil || arena.Game == nil {
		return ArenaViewerState{}, false
	}

	state := ArenaViewerState{
		ArenaID:        arena.ID,
		Game:           arena.Game.GetName(),
		GameState:      arena.Game.GetState(),
		MoveCount:      len(arena.MoveHistory),
		Status:         arena.Status,
		LastMoveAt:     arena.LastMove.UTC().Format(time.RFC3339Nano),
		WinnerPlayerID: arena.WinnerPlayerID,
		IsDraw:         arena.IsDraw,
	}

	if arena.Player1 != nil {
		state.Player1 = ArenaViewerPlayer{
			Name:   arena.Player1.BotName,
			BotID:  arena.Player1.BotID,
			Wins:   arena.Player1.Wins,
			Losses: arena.Player1.Losses,
			Draws:  arena.Player1.Draws,
		}
	}
	if arena.Player2 != nil {
		state.Player2 = ArenaViewerPlayer{
			Name:   arena.Player2.BotName,
			BotID:  arena.Player2.BotID,
			Wins:   arena.Player2.Wins,
			Losses: arena.Player2.Losses,
			Draws:  arena.Player2.Draws,
		}
	}

	return state, true
}
