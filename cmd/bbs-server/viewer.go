package main

import (
	"encoding/json"
	"fmt"
	"mime"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
	"github.com/JonKirkpatrick/bbs/stadium"
	"github.com/gorilla/websocket"
)

const viewerPluginEntryRoute = "/viewer/plugin-entry"

type viewerPageData struct {
	ArenaID  int
	MatchID  int
	AdminKey string
}

// viewerParticipant carries player identity and stats for the viewer UI.
type viewerParticipant struct {
	PlayerID int    `json:"player_id"`
	Name     string `json:"name"`
	BotID    string `json:"bot_id,omitempty"`
	Wins     int    `json:"wins"`
	Losses   int    `json:"losses"`
	Draws    int    `json:"draws"`
}

// rawViewerFrame is a minimal frame model with just raw game state and metadata.
// The client plugin renderer is responsible for parsing and rendering the raw state.
type rawViewerFrame struct {
	MoveIndex int    `json:"move_index"`
	Timestamp string `json:"timestamp,omitempty"`
	RawState  string `json:"raw_state"`
	// FrameStream is an optional, transport-friendly visual payload exposed by plugins.
	// When present, hosts can render this directly as pixels without executing plugin JS.
	FrameStream *viewerFrameStreamPacket `json:"frame_stream,omitempty"`
	IsTerminal  bool                     `json:"is_terminal"`
	Winner      string                   `json:"winner,omitempty"`
}

type viewerFrameStreamPacket struct {
	Version  int    `json:"version,omitempty"`
	MimeType string `json:"mime_type,omitempty"`
	Encoding string `json:"encoding,omitempty"`
	Data     string `json:"data,omitempty"`
	Width    int    `json:"width,omitempty"`
	Height   int    `json:"height,omitempty"`
	FrameID  string `json:"frame_id,omitempty"`
	KeyFrame bool   `json:"key_frame,omitempty"`
}

type viewerReplayResponse struct {
	MatchID         int                 `json:"match_id"`
	Game            string              `json:"game"`
	Frames          []rawViewerFrame    `json:"frames"`
	Players         []viewerParticipant `json:"players"`
	Plugin          *viewerPluginClient `json:"plugin,omitempty"`
	FrameCountTotal int                 `json:"frame_count_total"`
	FrameTruncated  bool                `json:"frame_truncated"`
}

type viewerPluginClient struct {
	EntryURL       string `json:"entry_url"`
	SupportsReplay bool   `json:"supports_replay"`
}

type viewerLiveEvent struct {
	ArenaID int                 `json:"arena_id"`
	Status  string              `json:"status"`
	Frame   rawViewerFrame      `json:"frame"`
	Players []viewerParticipant `json:"players"`
	Plugin  *viewerPluginClient `json:"plugin,omitempty"`
}

var viewerLiveWSUpgrader = websocket.Upgrader{
	ReadBufferSize:  1024,
	WriteBufferSize: 4096,
	CheckOrigin:     wsCheckOriginSameHost,
}

func handleViewerPage(w http.ResponseWriter, r *http.Request) {
	arenaID, _ := parsePositiveQueryInt(r, "arena_id")
	matchID, _ := parsePositiveQueryInt(r, "match_id")

	data := viewerPageData{
		ArenaID:  arenaID,
		MatchID:  matchID,
		AdminKey: strings.TrimSpace(r.URL.Query().Get("admin_key")),
	}

	if err := dashTemplates.ExecuteTemplate(w, "viewer.html", data); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
}

func handleViewerCanvasPage(w http.ResponseWriter, r *http.Request) {
	arenaID, _ := parsePositiveQueryInt(r, "arena_id")

	data := viewerPageData{
		ArenaID:  arenaID,
		MatchID:  0,
		AdminKey: strings.TrimSpace(r.URL.Query().Get("admin_key")),
	}

	if err := dashTemplates.ExecuteTemplate(w, "viewer-canvas.html", data); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
}

func handleViewerReplayData(w http.ResponseWriter, r *http.Request) {
	matchID, ok := parsePositiveQueryInt(r, "match_id")
	if !ok {
		http.Error(w, "match_id must be a positive integer", http.StatusBadRequest)
		return
	}

	maxFrames := 2000
	if raw := strings.TrimSpace(r.URL.Query().Get("max_frames")); raw != "" {
		parsed, err := strconv.Atoi(raw)
		if err != nil || parsed < 100 {
			http.Error(w, "max_frames must be an integer >= 100", http.StatusBadRequest)
			return
		}
		maxFrames = parsed
	}

	record, exists := stadium.DefaultManager.GetMatchRecord(matchID)
	if !exists {
		http.Error(w, "match not found", http.StatusNotFound)
		return
	}

	pluginClient := resolveViewerPluginClient(record.Game)
	if pluginClient == nil {
		http.Error(w, fmt.Sprintf("plugin viewer is not configured for game %q", record.Game), http.StatusBadRequest)
		return
	}

	frames, err := buildReplayRawFrames(record)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	frameCountTotal := len(frames)
	frames = downsampleReplayRawFrames(frames, maxFrames)

	resp := viewerReplayResponse{
		MatchID:         record.MatchID,
		Game:            record.Game,
		Frames:          frames,
		Players:         buildReplayParticipants(record),
		Plugin:          pluginClient,
		FrameCountTotal: frameCountTotal,
		FrameTruncated:  len(frames) < frameCountTotal,
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

func handleViewerLiveSSE(w http.ResponseWriter, r *http.Request) {
	arenaID, ok := parsePositiveQueryInt(r, "arena_id")
	if !ok {
		http.Error(w, "arena_id must be a positive integer", http.StatusBadRequest)
		return
	}

	initial, exists, err := buildLiveRawViewerEvent(arenaID)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if !exists {
		http.Error(w, "arena not found", http.StatusNotFound)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")

	if err := writeViewerSSEJSON(w, "frame", initial); err != nil {
		return
	}

	lastMoveIndex := initial.Frame.MoveIndex
	lastRawState := initial.Frame.RawState
	lastStatus := initial.Status
	flushTicker := time.NewTicker(resolveStreamFlushInterval(r, 100*time.Millisecond))
	defer flushTicker.Stop()
	var pendingEvent viewerLiveEvent
	pending := false

	sub := stadium.DefaultManager.Subscribe()
	defer stadium.DefaultManager.Unsubscribe(sub)

	for {
		select {
		case <-r.Context().Done():
			return
		case <-flushTicker.C:
			if !pending {
				continue
			}

			if err := writeViewerSSEJSON(w, "frame", pendingEvent); err != nil {
				return
			}

			lastMoveIndex = pendingEvent.Frame.MoveIndex
			lastRawState = pendingEvent.Frame.RawState
			lastStatus = pendingEvent.Status
			pending = false
		case _, alive := <-sub:
			if !alive {
				return
			}

			event, present, buildErr := buildLiveRawViewerEvent(arenaID)
			if buildErr != nil {
				_ = writeViewerSSEJSON(w, "error", map[string]string{"error": buildErr.Error()})
				return
			}
			if !present {
				_ = writeViewerSSEJSON(w, "closed", map[string]string{"message": "arena no longer active"})
				return
			}

			if event.Frame.MoveIndex == lastMoveIndex && event.Frame.RawState == lastRawState && event.Status == lastStatus {
				continue
			}

			pendingEvent = event
			pending = true
		}
	}
}

func handleViewerLiveWS(w http.ResponseWriter, r *http.Request) {
	arenaID, ok := parsePositiveQueryInt(r, "arena_id")
	if !ok {
		http.Error(w, "arena_id must be a positive integer", http.StatusBadRequest)
		return
	}

	initial, exists, err := buildLiveRawViewerEvent(arenaID)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if !exists {
		http.Error(w, "arena not found", http.StatusNotFound)
		return
	}

	conn, err := viewerLiveWSUpgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	defer conn.Close()

	if err := writeViewerWSJSON(conn, "frame", initial); err != nil {
		return
	}

	lastMoveIndex := initial.Frame.MoveIndex
	lastRawState := initial.Frame.RawState
	lastStatus := initial.Status
	flushTicker := time.NewTicker(resolveStreamFlushInterval(r, 100*time.Millisecond))
	defer flushTicker.Stop()
	var pendingEvent viewerLiveEvent
	pending := false

	sub := stadium.DefaultManager.Subscribe()
	defer stadium.DefaultManager.Unsubscribe(sub)

	for {
		select {
		case <-r.Context().Done():
			return
		case <-flushTicker.C:
			if !pending {
				continue
			}

			if err := writeViewerWSJSON(conn, "frame", pendingEvent); err != nil {
				return
			}

			lastMoveIndex = pendingEvent.Frame.MoveIndex
			lastRawState = pendingEvent.Frame.RawState
			lastStatus = pendingEvent.Status
			pending = false
		case _, alive := <-sub:
			if !alive {
				return
			}

			event, present, buildErr := buildLiveRawViewerEvent(arenaID)
			if buildErr != nil {
				_ = writeViewerWSJSON(conn, "error", map[string]string{"error": buildErr.Error()})
				return
			}
			if !present {
				_ = writeViewerWSJSON(conn, "closed", map[string]string{"message": "arena no longer active"})
				return
			}

			if event.Frame.MoveIndex == lastMoveIndex && event.Frame.RawState == lastRawState && event.Status == lastStatus {
				continue
			}

			pendingEvent = event
			pending = true
		}
	}
}

// buildReplayRawFrames reconstructs game frames from moves, collecting raw state only.
func buildReplayRawFrames(record stadium.MatchRecord) ([]rawViewerFrame, error) {
	args := append([]string(nil), record.GameArgs...)
	gameName := strings.ToLower(strings.TrimSpace(record.Game))
	game, err := games.GetGame(gameName, args)
	if err != nil {
		return nil, fmt.Errorf("failed to reconstruct game: %w", err)
	}

	frames := make([]rawViewerFrame, 0, len(record.Moves)+1)

	// Initial frame
	initialState := game.GetState()
	frames = append(frames, rawViewerFrame{
		MoveIndex:   0,
		Timestamp:   record.StartedAt,
		RawState:    initialState,
		FrameStream: extractFrameStreamPacket(initialState),
		IsTerminal:  false,
	})

	if len(record.Moves) > 0 {
		for i, move := range record.Moves {
			if err := game.ApplyMove(move.PlayerID, move.Move); err != nil {
				return nil, fmt.Errorf("failed to apply move %d (%s): %w", i+1, move.Move, err)
			}

			state := game.GetState()
			frames = append(frames, rawViewerFrame{
				MoveIndex:   i + 1,
				Timestamp:   move.OccurredAt,
				RawState:    state,
				FrameStream: extractFrameStreamPacket(state),
				IsTerminal:  false,
			})

			if over, _ := game.IsGameOver(); over {
				if episodic, ok := game.(games.EpisodicGame); ok {
					continued, _, advErr := episodic.AdvanceEpisode()
					if advErr != nil {
						return nil, fmt.Errorf("failed to advance episode after move %d: %w", i+1, advErr)
					}
					if continued {
						continue
					}
				}
			}
		}
	} else if len(record.MoveSequence) > 0 {
		// Fallback for older records without per-move metadata.
		currentPlayer := 1
		for i, move := range record.MoveSequence {
			if err := game.ApplyMove(currentPlayer, move); err != nil {
				return nil, fmt.Errorf("failed to apply fallback move %d (%s): %w", i+1, move, err)
			}

			state := game.GetState()
			frames = append(frames, rawViewerFrame{
				MoveIndex:   i + 1,
				RawState:    state,
				FrameStream: extractFrameStreamPacket(state),
				IsTerminal:  false,
			})

			if over, _ := game.IsGameOver(); over {
				if episodic, ok := game.(games.EpisodicGame); ok {
					continued, _, advErr := episodic.AdvanceEpisode()
					if advErr != nil {
						return nil, fmt.Errorf("failed to advance fallback episode after move %d: %w", i+1, advErr)
					}
					if continued {
						continue
					}
				}
			}

			if currentPlayer == 1 {
				currentPlayer = 2
			} else {
				currentPlayer = 1
			}
		}
	}

	if len(frames) > 0 {
		last := &frames[len(frames)-1]
		if record.TerminalStatus == "completed" || record.TerminalStatus == "aborted" {
			last.IsTerminal = true
		}
		if record.IsDraw {
			last.Winner = "draw"
		} else if record.WinnerPlayerID == 1 || record.WinnerPlayerID == 2 {
			last.Winner = fmt.Sprintf("player_%d", record.WinnerPlayerID)
		}
	}

	return frames, nil
}

// buildLiveRawViewerEvent builds a live viewer event with raw game state only.
func buildLiveRawViewerEvent(arenaID int) (viewerLiveEvent, bool, error) {
	arenaState, exists := stadium.DefaultManager.GetArenaViewerState(arenaID)
	if !exists {
		return viewerLiveEvent{}, false, nil
	}

	pluginClient := resolveViewerPluginClient(arenaState.Game)
	if pluginClient == nil {
		return viewerLiveEvent{}, false, fmt.Errorf("plugin viewer is not configured for game %q", arenaState.Game)
	}

	frame := rawViewerFrame{
		MoveIndex:   arenaState.MoveCount,
		Timestamp:   arenaState.LastMoveAt,
		RawState:    arenaState.GameState,
		FrameStream: extractFrameStreamPacket(arenaState.GameState),
		IsTerminal:  false,
	}

	if arenaState.Status == "completed" || arenaState.Status == "aborted" {
		frame.IsTerminal = true
		if arenaState.IsDraw {
			frame.Winner = "draw"
		} else if arenaState.WinnerPlayerID == 1 || arenaState.WinnerPlayerID == 2 {
			frame.Winner = fmt.Sprintf("player_%d", arenaState.WinnerPlayerID)
		}
	}

	return viewerLiveEvent{
		ArenaID: arenaState.ArenaID,
		Status:  arenaState.Status,
		Frame:   frame,
		Players: buildLiveParticipants(arenaState),
		Plugin:  pluginClient,
	}, true, nil
}

func buildLiveParticipants(arenaState stadium.ArenaViewerState) []viewerParticipant {
	players := make([]viewerParticipant, 0, 2)
	if arenaState.Player1.Name != "" {
		players = append(players, viewerParticipant{
			PlayerID: 1,
			Name:     arenaState.Player1.Name,
			BotID:    arenaState.Player1.BotID,
			Wins:     arenaState.Player1.Wins,
			Losses:   arenaState.Player1.Losses,
			Draws:    arenaState.Player1.Draws,
		})
	}
	if arenaState.Player2.Name != "" {
		players = append(players, viewerParticipant{
			PlayerID: 2,
			Name:     arenaState.Player2.Name,
			BotID:    arenaState.Player2.BotID,
			Wins:     arenaState.Player2.Wins,
			Losses:   arenaState.Player2.Losses,
			Draws:    arenaState.Player2.Draws,
		})
	}
	return players
}

func buildReplayParticipants(record stadium.MatchRecord) []viewerParticipant {
	players := make([]viewerParticipant, 0, 2)
	for _, p := range []struct {
		id  int
		par stadium.MatchParticipant
	}{
		{1, record.Player1},
		{2, record.Player2},
	} {
		if p.par.BotName == "" && p.par.BotID == "" {
			continue
		}
		w, l, d := stadium.DefaultManager.BotStatsForID(p.par.BotID)
		players = append(players, viewerParticipant{
			PlayerID: p.id,
			Name:     p.par.BotName,
			BotID:    p.par.BotID,
			Wins:     w,
			Losses:   l,
			Draws:    d,
		})
	}
	return players
}

func writeViewerSSEJSON(w http.ResponseWriter, eventName string, payload interface{}) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}

	if _, err := fmt.Fprintf(w, "event: %s\n", eventName); err != nil {
		return err
	}
	if _, err := fmt.Fprintf(w, "data: %s\n\n", data); err != nil {
		return err
	}

	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}

	return nil
}

func writeViewerWSJSON(conn *websocket.Conn, eventName string, payload interface{}) error {
	return writeStreamWSEvent(conn, "viewer", eventName, payload)
}

func resolveViewerPluginClient(gameName string) *viewerPluginClient {
	lookup := strings.ToLower(strings.TrimSpace(gameName))
	if lookup == "" {
		return nil
	}

	catalog := games.AvailableGameCatalog()
	for _, entry := range catalog {
		if strings.EqualFold(entry.Name, lookup) {
			if strings.TrimSpace(entry.ViewerClientEntry) == "" {
				return nil
			}
			return &viewerPluginClient{
				EntryURL:       viewerPluginEntryRoute + "?game=" + url.QueryEscape(entry.Name),
				SupportsReplay: entry.SupportsReplay,
			}
		}
	}

	return nil
}

func handleViewerPluginEntry(w http.ResponseWriter, r *http.Request) {
	gameName := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("game")))
	if gameName == "" {
		http.Error(w, "game is required", http.StatusBadRequest)
		return
	}

	filePath, ok := games.PluginViewerClientFile(gameName)
	if !ok {
		http.Error(w, "plugin viewer entry not found", http.StatusNotFound)
		return
	}

	raw, err := os.ReadFile(filePath)
	if err != nil {
		http.Error(w, "failed reading plugin viewer entry", http.StatusInternalServerError)
		return
	}

	contentType := mime.TypeByExtension(filepath.Ext(filePath))
	if contentType == "" {
		contentType = "application/javascript"
	}

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(raw)
}

func parsePositiveQueryInt(r *http.Request, key string) (int, bool) {
	raw := strings.TrimSpace(r.URL.Query().Get(key))
	if raw == "" {
		return 0, false
	}
	parsed, err := strconv.Atoi(raw)
	if err != nil || parsed <= 0 {
		return 0, false
	}
	return parsed, true
}

func downsampleReplayRawFrames(frames []rawViewerFrame, maxFrames int) []rawViewerFrame {
	if maxFrames <= 0 || len(frames) <= maxFrames {
		return frames
	}

	if maxFrames == 1 {
		return []rawViewerFrame{frames[len(frames)-1]}
	}

	out := make([]rawViewerFrame, 0, maxFrames)
	lastIdx := len(frames) - 1

	for i := 0; i < maxFrames-1; i++ {
		idx := i * lastIdx / (maxFrames - 1)
		out = append(out, frames[idx])
	}

	out = append(out, frames[lastIdx])
	return out
}

func extractFrameStreamPacket(rawState string) *viewerFrameStreamPacket {
	rawState = strings.TrimSpace(rawState)
	if rawState == "" {
		return nil
	}

	var root map[string]interface{}
	if err := json.Unmarshal([]byte(rawState), &root); err != nil {
		return nil
	}

	viewer, ok := root["viewer"].(map[string]interface{})
	if !ok {
		return nil
	}

	rawPacket, ok := viewer["frame_stream"].(map[string]interface{})
	if !ok {
		return nil
	}

	packet := &viewerFrameStreamPacket{}

	if v, ok := toInt(rawPacket["version"]); ok {
		packet.Version = v
	}
	if v, ok := toString(rawPacket["mime_type"]); ok {
		packet.MimeType = v
	}
	if v, ok := toString(rawPacket["encoding"]); ok {
		packet.Encoding = strings.ToLower(v)
	}
	if v, ok := toString(rawPacket["data"]); ok {
		packet.Data = v
	}
	if v, ok := toInt(rawPacket["width"]); ok {
		packet.Width = v
	}
	if v, ok := toInt(rawPacket["height"]); ok {
		packet.Height = v
	}
	if v, ok := toString(rawPacket["frame_id"]); ok {
		packet.FrameID = v
	}
	if v, ok := rawPacket["key_frame"].(bool); ok {
		packet.KeyFrame = v
	}

	if packet.MimeType == "" || packet.Encoding == "" || packet.Data == "" {
		return nil
	}

	if packet.Encoding != "base64" && packet.Encoding != "utf8" && packet.Encoding != "data_url" {
		return nil
	}

	return packet
}

func toString(value interface{}) (string, bool) {
	v, ok := value.(string)
	if !ok {
		return "", false
	}
	v = strings.TrimSpace(v)
	if v == "" {
		return "", false
	}
	return v, true
}

func toInt(value interface{}) (int, bool) {
	switch n := value.(type) {
	case float64:
		return int(n), true
	case int:
		return n, true
	case int64:
		return int(n), true
	default:
		return 0, false
	}
}
