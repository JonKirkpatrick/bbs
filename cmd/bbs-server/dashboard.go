package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"html/template"
	"net"
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

var dashTemplates *template.Template

var dashboardWSUpgrader = websocket.Upgrader{
	ReadBufferSize:  1024,
	WriteBufferSize: 8192,
	CheckOrigin:     wsCheckOriginSameHost,
}

const dashboardAdminKeyEnv = "BBS_DASHBOARD_ADMIN_KEY"
const mockFederationReceiverEnabledEnv = "BBS_ENABLE_MOCK_FEDERATION_RECEIVER"
const mockFederationReceiverTokenEnv = "BBS_MOCK_FEDERATION_RECEIVER_TOKEN"

type dashboardIndexData struct {
	AdminConfigured  bool
	IsAdmin          bool
	AdminKey         string
	OwnerToken       string
	SSEQuery         string
	DashboardVersion string
	GameCatalogJSON  template.JS
}

type dashboardStateView struct {
	Snapshot        stadium.ManagerSnapshot
	IsAdmin         bool
	AdminConfigured bool
	AdminKey        string
	OwnerToken      string
	OwnerSession    stadium.SessionSnapshot
	OwnerConnected  bool
	BotHost         string
	BotPort         string
	BotEndpoint     string
	GameCatalog     []games.GameCatalogEntry
	PluginInfo      games.PluginDiagnostics
}

type apiArenaView struct {
	ArenaID        int    `json:"arena_id"`
	Game           string `json:"game"`
	Status         string `json:"status"`
	Player1Name    string `json:"player1_name,omitempty"`
	Player2Name    string `json:"player2_name,omitempty"`
	MoveCount      int    `json:"move_count"`
	GameState      string `json:"game_state"`
	ViewerURL      string `json:"viewer_url"`
	PluginEntryURL string `json:"plugin_entry_url,omitempty"`
	ViewerWidth    int    `json:"viewer_width"`
	ViewerHeight   int    `json:"viewer_height"`
}

func resolveArenaViewerDimensions(gameName, rawState string) (int, int) {
	game := strings.TrimSpace(strings.ToLower(gameName))
	switch game {
	case "guess_number":
		return 760, 300
	case "gridworld_rl":
		return resolveGridworldViewerDimensions(rawState)
	default:
		return 760, 340
	}
}

func resolveGridworldViewerDimensions(rawState string) (int, int) {
	const defaultWidth = 760
	const defaultHeight = 320

	if strings.TrimSpace(rawState) == "" {
		return defaultWidth, defaultHeight
	}

	var payload struct {
		MapRows []string `json:"map_rows"`
	}

	if err := json.Unmarshal([]byte(rawState), &payload); err != nil {
		return defaultWidth, defaultHeight
	}

	if len(payload.MapRows) == 0 {
		return defaultWidth, defaultHeight
	}

	height := len(payload.MapRows)
	width := len(strings.TrimSpace(payload.MapRows[0]))
	if width <= 0 {
		return defaultWidth, defaultHeight
	}

	maxSide := width
	if height > maxSide {
		maxSide = height
	}

	tile := 460 / maxSide
	if tile < 26 {
		tile = 26
	}

	gridWidth := tile * width
	gridHeight := tile * height
	sidebarX := gridWidth + 36

	viewerWidth := sidebarX + 240
	if viewerWidth < 760 {
		viewerWidth = 760
	}

	viewerHeight := gridHeight + 56
	if viewerHeight < 340 {
		viewerHeight = 340
	}

	return viewerWidth, viewerHeight
}

func initDashboard() {
	paths, err := getRuntimePaths()
	if err != nil {
		panic(fmt.Sprintf("resolve runtime paths: %v", err))
	}
	pattern := filepath.Join(paths.TemplatesDir, "*.html")
	files, _ := filepath.Glob(pattern)
	fmt.Printf("Found %d dashboard templates in %s: %v\n", len(files), paths.TemplatesDir, files)
	funcMap := template.FuncMap{
		"fmtTime":    formatDashboardTime,
		"fmtSeconds": formatMillisecondsAsSeconds,
	}
	dashTemplates = template.Must(template.New("").Funcs(funcMap).ParseGlob(pattern))
}

func formatDashboardTime(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}

	ts, err := time.Parse(time.RFC3339Nano, raw)
	if err != nil {
		return raw
	}

	return ts.UTC().Truncate(time.Second).Format("2006-01-02 15:04:05 UTC")
}

func formatMillisecondsAsSeconds(ms int64) string {
	if ms <= 0 {
		return "0s"
	}

	seconds := (time.Duration(ms) * time.Millisecond).Round(time.Second) / time.Second
	if seconds < 1 {
		seconds = 1
	}

	return fmt.Sprintf("%ds", seconds)
}

func dashboardAdminKey() string {
	return strings.TrimSpace(os.Getenv(dashboardAdminKeyEnv))
}

func isAdminAuthorized(candidate string) bool {
	configured := dashboardAdminKey()
	if configured == "" || candidate == "" {
		return false
	}

	return subtle.ConstantTimeCompare([]byte(candidate), []byte(configured)) == 1
}

func parseGameArgs(raw string) []string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil
	}

	if strings.Contains(raw, ",") {
		parts := strings.Split(raw, ",")
		args := make([]string, 0, len(parts))
		for _, part := range parts {
			value := strings.TrimSpace(part)
			if value != "" {
				args = append(args, value)
			}
		}
		return args
	}

	return strings.Fields(raw)
}

func renderActionResult(w http.ResponseWriter, ok bool, message string) {
	class := "admin-result error"
	if ok {
		class = "admin-result success"
	}
	fmt.Fprintf(w, `<div class="%s">%s</div>`, class, template.HTMLEscapeString(message))
}

func ownerTokenFromRequest(r *http.Request) string {
	ownerToken := strings.TrimSpace(r.FormValue("owner_token"))
	if ownerToken == "" {
		ownerToken = strings.TrimSpace(r.URL.Query().Get("owner_token"))
	}
	return ownerToken
}

func dashboardSSEQuery(adminKey, ownerToken string) string {
	values := url.Values{}
	if strings.TrimSpace(adminKey) != "" {
		values.Set("admin_key", strings.TrimSpace(adminKey))
	}
	if strings.TrimSpace(ownerToken) != "" {
		values.Set("owner_token", strings.TrimSpace(ownerToken))
	}
	encoded := values.Encode()
	if encoded == "" {
		return ""
	}
	return "?" + encoded
}

func requestHostOnly(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return "localhost"
	}

	if host, port, err := net.SplitHostPort(raw); err == nil {
		if host != "" && port != "" {
			return host
		}
	}

	if strings.Count(raw, ":") == 1 {
		parts := strings.SplitN(raw, ":", 2)
		if parts[0] != "" {
			return parts[0]
		}
	}

	return raw
}

func dashboardBotHost(r *http.Request) string {
	return requestHostOnly(r.Host)
}

func dashboardBotEndpoint(r *http.Request) string {
	return net.JoinHostPort(dashboardBotHost(r), botServerPort)
}

func marshalJSONForTemplate(value interface{}) template.JS {
	encoded, err := json.Marshal(value)
	if err != nil {
		return template.JS("[]")
	}
	return template.JS(encoded)
}

func buildDashboardStateView(r *http.Request, snapshot stadium.ManagerSnapshot, adminKey, ownerToken string) dashboardStateView {
	view := dashboardStateView{
		Snapshot:        snapshot,
		IsAdmin:         isAdminAuthorized(adminKey),
		AdminConfigured: dashboardAdminKey() != "",
		AdminKey:        adminKey,
		OwnerToken:      ownerToken,
		BotHost:         dashboardBotHost(r),
		BotPort:         botServerPort,
		BotEndpoint:     dashboardBotEndpoint(r),
		GameCatalog:     games.AvailableGameCatalog(),
		PluginInfo:      games.CurrentPluginDiagnostics(),
	}

	if ownerToken != "" {
		if session, ok := stadium.DefaultManager.OwnerSessionSnapshot(ownerToken); ok {
			view.OwnerSession = session
			view.OwnerConnected = true
		}
	}

	return view
}

func writeDashboardSSE(w http.ResponseWriter, view dashboardStateView) error {
	html, err := renderDashboardStateHTML(view)
	if err != nil {
		return err
	}

	fmt.Fprintf(w, "event: state\n")
	for _, line := range strings.Split(html, "\n") {
		fmt.Fprintf(w, "data: %s\n", line)
	}
	_, err = fmt.Fprintf(w, "\n")
	if err != nil {
		return err
	}

	if f, ok := w.(http.Flusher); ok {
		f.Flush()
	}

	return nil
}

func renderDashboardStateHTML(view dashboardStateView) (string, error) {
	var buf bytes.Buffer
	if err := dashTemplates.ExecuteTemplate(&buf, "dashboard-state.html", view); err != nil {
		return "", err
	}
	return buf.String(), nil
}

func writeDashboardWS(conn *websocket.Conn, view dashboardStateView) error {
	html, err := renderDashboardStateHTML(view)
	if err != nil {
		return err
	}

	return writeStreamWSEvent(conn, "dashboard", "state", map[string]interface{}{"html": html})
}

func handleDashboardSSE(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")

	adminKey := r.URL.Query().Get("admin_key")
	ownerToken := ownerTokenFromRequest(r)

	sub := stadium.DefaultManager.Subscribe()
	defer stadium.DefaultManager.Unsubscribe(sub)

	view := buildDashboardStateView(r, stadium.DefaultManager.Snapshot(), adminKey, ownerToken)

	if err := writeDashboardSSE(w, view); err != nil {
		return
	}

	flushTicker := time.NewTicker(resolveStreamFlushInterval(r, 200*time.Millisecond))
	defer flushTicker.Stop()
	pending := false

	for {
		select {
		case <-r.Context().Done():
			return
		case _, ok := <-sub:
			if !ok {
				return
			}
			pending = true
		case <-flushTicker.C:
			if !pending {
				continue
			}

			view = buildDashboardStateView(r, stadium.DefaultManager.Snapshot(), adminKey, ownerToken)

			if err := writeDashboardSSE(w, view); err != nil {
				return
			}
			pending = false
		}
	}
}

func handleDashboardWS(w http.ResponseWriter, r *http.Request) {
	adminKey := r.URL.Query().Get("admin_key")
	ownerToken := ownerTokenFromRequest(r)

	conn, err := dashboardWSUpgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	defer conn.Close()

	sub := stadium.DefaultManager.Subscribe()
	defer stadium.DefaultManager.Unsubscribe(sub)

	view := buildDashboardStateView(r, stadium.DefaultManager.Snapshot(), adminKey, ownerToken)
	if err := writeDashboardWS(conn, view); err != nil {
		return
	}

	flushTicker := time.NewTicker(resolveStreamFlushInterval(r, 200*time.Millisecond))
	defer flushTicker.Stop()
	pending := false

	for {
		select {
		case <-r.Context().Done():
			return
		case _, ok := <-sub:
			if !ok {
				return
			}
			pending = true
		case <-flushTicker.C:
			if !pending {
				continue
			}

			view = buildDashboardStateView(r, stadium.DefaultManager.Snapshot(), adminKey, ownerToken)
			if err := writeDashboardWS(conn, view); err != nil {
				return
			}
			pending = false
		}
	}
}

func handleIndex(w http.ResponseWriter, r *http.Request) {
	adminKey := r.URL.Query().Get("admin_key")
	ownerToken := ownerTokenFromRequest(r)
	catalog := games.AvailableGameCatalog()
	data := dashboardIndexData{
		AdminConfigured:  dashboardAdminKey() != "",
		IsAdmin:          isAdminAuthorized(adminKey),
		AdminKey:         adminKey,
		OwnerToken:       ownerToken,
		SSEQuery:         dashboardSSEQuery(adminKey, ownerToken),
		DashboardVersion: currentDashboardVersion(),
		GameCatalogJSON:  marshalJSONForTemplate(catalog),
	}
	if err := dashTemplates.ExecuteTemplate(w, "index.html", data); err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		fmt.Fprintf(w, "Template error: %v\n", err)
	}
}

func requireAdmin(w http.ResponseWriter, r *http.Request) (string, bool) {
	adminKey := adminKeyFromRequest(r)

	if !isAdminAuthorized(adminKey) {
		renderActionResult(w, false, "Admin authorization failed. Set BBS_DASHBOARD_ADMIN_KEY and provide a valid key.")
		return "", false
	}

	return adminKey, true
}

func adminKeyFromRequest(r *http.Request) string {
	adminKey := strings.TrimSpace(r.FormValue("admin_key"))
	if adminKey == "" {
		adminKey = strings.TrimSpace(r.URL.Query().Get("admin_key"))
	}
	return adminKey
}

func requireAdminAPI(w http.ResponseWriter, r *http.Request) bool {
	if !isAdminAuthorized(adminKeyFromRequest(r)) {
		writeJSON(w, http.StatusForbidden, map[string]interface{}{
			"status":  "err",
			"message": "admin authorization failed",
		})
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, statusCode int, payload interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(statusCode)
	_ = json.NewEncoder(w).Encode(payload)
}

func handleAPIStatus(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "ok",
	})
}

func handleAPIGameCatalog(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}

	writeJSON(w, http.StatusOK, games.AvailableGameCatalog())
}

func handleAPIArenas(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}

	snapshot := stadium.DefaultManager.Snapshot()
	host := dashboardBotHost(r)
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	viewerBase := fmt.Sprintf("%s://%s:%s/viewer/canvas", scheme, host, dashboardServerPort)

	arenas := make([]apiArenaView, 0, len(snapshot.Arenas))
	for _, arena := range snapshot.Arenas {
		viewerWidth, viewerHeight := resolveArenaViewerDimensions(arena.Game, arena.GameState)

		item := apiArenaView{
			ArenaID:      arena.ID,
			Game:         arena.Game,
			Status:       arena.Status,
			Player1Name:  arena.Player1Name,
			Player2Name:  arena.Player2Name,
			MoveCount:    arena.MoveCount,
			GameState:    arena.GameState,
			ViewerURL:    fmt.Sprintf("%s?arena_id=%d", viewerBase, arena.ID),
			ViewerWidth:  viewerWidth,
			ViewerHeight: viewerHeight,
		}

		if plugin := resolveViewerPluginClient(arena.Game); plugin != nil {
			item.PluginEntryURL = plugin.EntryURL
		}

		arenas = append(arenas, item)
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "ok",
		"arenas": arenas,
	})
}

func boolEnv(name string) bool {
	v := strings.TrimSpace(strings.ToLower(os.Getenv(name)))
	return v == "1" || v == "true" || v == "yes" || v == "on"
}

func mockFederationReceiverEnabled() bool {
	return boolEnv(mockFederationReceiverEnabledEnv)
}

func mockFederationReceiverToken() string {
	return strings.TrimSpace(os.Getenv(mockFederationReceiverTokenEnv))
}

func tokenFromBearerHeader(r *http.Request) string {
	auth := strings.TrimSpace(r.Header.Get("Authorization"))
	if auth == "" {
		return ""
	}
	parts := strings.SplitN(auth, " ", 2)
	if len(parts) != 2 || !strings.EqualFold(parts[0], "Bearer") {
		return ""
	}
	return strings.TrimSpace(parts[1])
}

type mockFederationIngestRequest struct {
	EventID        string          `json:"event_id"`
	OriginServerID string          `json:"origin_server_id"`
	EventType      string          `json:"event_type"`
	Payload        json.RawMessage `json:"payload,omitempty"`
	PayloadJSON    string          `json:"payload_json,omitempty"`
	CreatedAt      string          `json:"created_at,omitempty"`
}

func handleMockFederationIngest(w http.ResponseWriter, r *http.Request) {
	if !mockFederationReceiverEnabled() {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}

	if expected := mockFederationReceiverToken(); expected != "" {
		provided := tokenFromBearerHeader(r)
		if provided == "" {
			provided = strings.TrimSpace(r.URL.Query().Get("token"))
		}
		if subtle.ConstantTimeCompare([]byte(expected), []byte(provided)) != 1 {
			writeJSON(w, http.StatusUnauthorized, map[string]interface{}{
				"status":  "err",
				"message": "mock federation receiver token is invalid",
			})
			return
		}
	}

	var req mockFederationIngestRequest
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]interface{}{
			"status":  "err",
			"message": "invalid federation payload",
		})
		return
	}

	req.EventID = strings.TrimSpace(req.EventID)
	req.OriginServerID = strings.TrimSpace(req.OriginServerID)
	if req.EventID == "" || req.OriginServerID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]interface{}{
			"status":  "err",
			"message": "event_id and origin_server_id are required",
		})
		return
	}

	duplicate, err := stadium.DefaultManager.RecordInboundFederationEvent(context.Background(), req.OriginServerID, req.EventID, time.Now().UTC())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]interface{}{
			"status":  "err",
			"message": err.Error(),
		})
		return
	}

	writeJSON(w, http.StatusAccepted, map[string]interface{}{
		"status":    "accepted",
		"ack_id":    req.EventID,
		"event_id":  req.EventID,
		"duplicate": duplicate,
	})
}

func parseLimit(raw string, fallback int) int {
	if fallback <= 0 {
		fallback = 25
	}
	v := strings.TrimSpace(raw)
	if v == "" {
		return fallback
	}
	parsed, err := strconv.Atoi(v)
	if err != nil || parsed <= 0 {
		return fallback
	}
	if parsed > 250 {
		return 250
	}
	return parsed
}

func handleAdminDebugServerIdentity(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if !requireAdminAPI(w, r) {
		return
	}

	identity, found, err := stadium.DefaultManager.DurableServerIdentity(context.Background())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]interface{}{
			"status":  "err",
			"message": err.Error(),
		})
		return
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "ok",
		"found":  found,
		"identity": map[string]interface{}{
			"local_server_id":        identity.LocalServerID,
			"global_server_id":       identity.GlobalServerID,
			"preferred_display_name": identity.PreferredDisplayName,
			"accepted_display_name":  identity.AcceptedDisplayName,
			"registry_status":        identity.RegistryStatus,
			"created_at":             identity.CreatedAt.UTC().Format(time.RFC3339Nano),
			"last_registration_at":   identity.LastRegistrationAt.UTC().Format(time.RFC3339Nano),
		},
	})
}

func handleAdminDebugOutbox(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if !requireAdminAPI(w, r) {
		return
	}

	limit := parseLimit(r.URL.Query().Get("limit"), 50)
	events, err := stadium.DefaultManager.PendingOutboxEvents(context.Background(), limit, time.Now().UTC())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]interface{}{
			"status":  "err",
			"message": err.Error(),
		})
		return
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "ok",
		"count":  len(events),
		"events": events,
	})
}

func handleAdminDebugRecentMatches(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if !requireAdminAPI(w, r) {
		return
	}

	botID := strings.TrimSpace(r.URL.Query().Get("bot_id"))
	if botID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]interface{}{
			"status":  "err",
			"message": "bot_id is required",
		})
		return
	}

	limit := parseLimit(r.URL.Query().Get("limit"), 25)
	matches, err := stadium.DefaultManager.RecentDurableMatchesForBot(context.Background(), botID, limit)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]interface{}{
			"status":  "err",
			"message": err.Error(),
		})
		return
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status":  "ok",
		"bot_id":  botID,
		"count":   len(matches),
		"matches": matches,
	})
}

func requireOwnerToken(w http.ResponseWriter, r *http.Request) (string, bool) {
	ownerToken := ownerTokenFromRequest(r)
	if ownerToken == "" {
		renderActionResult(w, false, "No dashboard control token provided. Use Register Bot first.")
		return "", false
	}

	return ownerToken, true
}

func optionalSessionIDFromRequest(r *http.Request) int {
	raw := strings.TrimSpace(r.FormValue("session_id"))
	if raw == "" {
		raw = strings.TrimSpace(r.URL.Query().Get("session_id"))
	}
	if raw == "" {
		return 0
	}

	id, err := strconv.Atoi(raw)
	if err != nil || id <= 0 {
		return 0
	}

	return id
}

func handleAPIOwnerToken(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]interface{}{
			"status":  "err",
			"message": "use GET or POST",
		})
		return
	}

	ownerToken := ownerTokenFromRequest(r)
	if ownerToken == "" {
		issued, err := stadium.NewOwnerToken()
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]interface{}{
				"status":  "err",
				"message": "failed to generate owner token",
			})
			return
		}
		ownerToken = issued
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status":      "ok",
		"owner_token": ownerToken,
	})
}

func handleOwnerRegisterBot(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST to issue a bot control token.")
		return
	}

	ownerToken, err := stadium.NewOwnerToken()
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		renderActionResult(w, false, "Failed to generate a bot control token.")
		return
	}

	http.Redirect(w, r, "/"+dashboardSSEQuery(strings.TrimSpace(r.FormValue("admin_key")), ownerToken), http.StatusSeeOther)
}

func handleOwnerCreateArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for bot control actions.")
		return
	}

	ownerToken, ok := requireOwnerToken(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	gameType := strings.ToLower(strings.TrimSpace(r.FormValue("game")))
	if gameType == "" {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Game type is required.")
		return
	}

	args := parseGameArgs(r.FormValue("game_args"))
	game, err := games.GetGame(gameType, args)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	timeLimit, allowHandicap, err := resolveArenaRuntimeOptions(game, r.FormValue("time_ms"), r.FormValue("allow_handicap"))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	arenaID, err := stadium.DefaultManager.CreateArenaForOwner(ownerToken, game, args, timeLimit, allowHandicap)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	renderActionResult(w, true, fmt.Sprintf("Created arena %d (%s).", arenaID, gameType))
}

func handleOwnerJoinArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for bot control actions.")
		return
	}

	ownerToken, ok := requireOwnerToken(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	arenaID, err := strconv.Atoi(strings.TrimSpace(r.FormValue("arena_id")))
	if err != nil || arenaID <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Arena ID must be a positive integer.")
		return
	}

	handicap, err := strconv.Atoi(strings.TrimSpace(r.FormValue("handicap_percent")))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Handicap must be an integer.")
		return
	}

	sessionID := optionalSessionIDFromRequest(r)
	var joinErr error
	if sessionID > 0 {
		joinErr = stadium.DefaultManager.JoinArenaForOwnerSession(ownerToken, sessionID, arenaID, handicap)
	} else {
		joinErr = stadium.DefaultManager.JoinArenaForOwner(ownerToken, arenaID, handicap)
	}

	if joinErr != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, joinErr.Error())
		return
	}

	renderActionResult(w, true, fmt.Sprintf("Requested join for arena %d.", arenaID))
}

func handleOwnerLeaveArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for bot control actions.")
		return
	}

	ownerToken, ok := requireOwnerToken(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	sessionID := optionalSessionIDFromRequest(r)
	var leaveErr error
	if sessionID > 0 {
		leaveErr = stadium.DefaultManager.LeaveArenaForOwnerSession(ownerToken, sessionID)
	} else {
		leaveErr = stadium.DefaultManager.LeaveArenaForOwner(ownerToken)
	}

	if leaveErr != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, leaveErr.Error())
		return
	}

	renderActionResult(w, true, "Bot left the arena. It remains connected and can join another.")
}

func handleOwnerEjectBot(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for bot control actions.")
		return
	}

	ownerToken, ok := requireOwnerToken(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	reason := strings.TrimSpace(r.FormValue("reason"))
	sessionID := optionalSessionIDFromRequest(r)
	var ejectErr error
	if sessionID > 0 {
		ejectErr = stadium.DefaultManager.EjectOwnerSessionBySessionID(ownerToken, sessionID, reason)
	} else {
		ejectErr = stadium.DefaultManager.EjectOwnerSession(ownerToken, reason)
	}

	if ejectErr != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, ejectErr.Error())
		return
	}

	renderActionResult(w, true, "Owned bot disconnected.")
}

func handleAdminEjectBot(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for admin actions.")
		return
	}

	_, ok := requireAdmin(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	sessionID, err := strconv.Atoi(strings.TrimSpace(r.FormValue("session_id")))
	if err != nil || sessionID <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Invalid session ID.")
		return
	}

	reason := strings.TrimSpace(r.FormValue("reason"))
	if err := stadium.DefaultManager.EjectSession(sessionID, reason); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	renderActionResult(w, true, fmt.Sprintf("Session %d ejected.", sessionID))
}

func handleAdminLeaveArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for admin actions.")
		return
	}

	_, ok := requireAdmin(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	sessionID, err := strconv.Atoi(strings.TrimSpace(r.FormValue("session_id")))
	if err != nil || sessionID <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Invalid session ID.")
		return
	}

	if err := stadium.DefaultManager.LeaveArenaForSession(sessionID); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	renderActionResult(w, true, fmt.Sprintf("Session %d left its arena. Connection remains open.", sessionID))
}

func handleAdminJoinArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for admin actions.")
		return
	}

	_, ok := requireAdmin(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	sessionID, err := strconv.Atoi(strings.TrimSpace(r.FormValue("session_id")))
	if err != nil || sessionID <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Invalid session ID.")
		return
	}

	arenaID, err := strconv.Atoi(strings.TrimSpace(r.FormValue("arena_id")))
	if err != nil || arenaID <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Arena ID must be a positive integer.")
		return
	}

	handicap, err := strconv.Atoi(strings.TrimSpace(r.FormValue("handicap_percent")))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Handicap must be an integer.")
		return
	}

	if err := stadium.DefaultManager.JoinArenaForSession(sessionID, arenaID, handicap); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	renderActionResult(w, true, fmt.Sprintf("Session %d joined arena %d.", sessionID, arenaID))
}

func handleAdminCreateArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderActionResult(w, false, "Use POST for admin actions.")
		return
	}

	_, ok := requireAdmin(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	gameType := strings.ToLower(strings.TrimSpace(r.FormValue("game")))
	if gameType == "" {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, "Game type is required.")
		return
	}

	args := parseGameArgs(r.FormValue("game_args"))
	game, err := games.GetGame(gameType, args)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	timeLimit, allowHandicap, err := resolveArenaRuntimeOptions(game, r.FormValue("time_ms"), r.FormValue("allow_handicap"))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
		return
	}

	arenaID := stadium.DefaultManager.CreateArena(game, args, timeLimit, allowHandicap)
	renderActionResult(w, true, fmt.Sprintf("Created arena %d (%s).", arenaID, gameType))
}

func startDashboard() {
	initDashboard()

	mux := http.NewServeMux()
	mux.HandleFunc("/dashboard-sse", handleDashboardSSE)
	mux.HandleFunc("/dashboard-ws", handleDashboardWS)
	mux.HandleFunc("/api/status", handleAPIStatus)
	mux.HandleFunc("/api/owner-token", handleAPIOwnerToken)
	mux.HandleFunc("/api/game-catalog", handleAPIGameCatalog)
	mux.HandleFunc("/api/arenas", handleAPIArenas)
	mux.HandleFunc("/viewer", handleViewerPage)
	mux.HandleFunc("/viewer/canvas", handleViewerCanvasPage)
	mux.HandleFunc("/viewer/live-sse", handleViewerLiveSSE)
	mux.HandleFunc("/viewer/live-ws", handleViewerLiveWS)
	mux.HandleFunc("/viewer/plugin-entry", handleViewerPluginEntry)
	mux.HandleFunc("/viewer/replay-data", handleViewerReplayData)
	mux.HandleFunc("/owner/register-bot", handleOwnerRegisterBot)
	mux.HandleFunc("/owner/create-arena", handleOwnerCreateArena)
	mux.HandleFunc("/owner/join-arena", handleOwnerJoinArena)
	mux.HandleFunc("/owner/leave-arena", handleOwnerLeaveArena)
	mux.HandleFunc("/owner/eject-bot", handleOwnerEjectBot)
	mux.HandleFunc("/admin/leave-arena", handleAdminLeaveArena)
	mux.HandleFunc("/admin/join-arena", handleAdminJoinArena)
	mux.HandleFunc("/admin/eject-bot", handleAdminEjectBot)
	mux.HandleFunc("/admin/create-arena", handleAdminCreateArena)
	mux.HandleFunc("/admin/debug/server-identity", handleAdminDebugServerIdentity)
	mux.HandleFunc("/admin/debug/outbox", handleAdminDebugOutbox)
	mux.HandleFunc("/admin/debug/recent-matches", handleAdminDebugRecentMatches)
	mux.HandleFunc("/federation/mock/ingest", handleMockFederationIngest)
	mux.HandleFunc("/", handleIndex)

	fmt.Printf("Dashboard running at http://localhost:%s\n", dashboardServerPort)
	if err := http.ListenAndServe(":"+dashboardServerPort, mux); err != nil {
		fmt.Printf("Dashboard server error: %s\n", err)
	}
}
