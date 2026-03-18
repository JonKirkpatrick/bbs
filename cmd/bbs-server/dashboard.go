package main

import (
	"bytes"
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
)

var dashTemplates *template.Template

const dashboardAdminKeyEnv = "BBS_DASHBOARD_ADMIN_KEY"

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

func initDashboard() {
	files, _ := filepath.Glob("templates/*.html")
	fmt.Printf("Found %d dashboard templates: %v\n", len(files), files)
	funcMap := template.FuncMap{
		"fmtTime":    formatDashboardTime,
		"fmtSeconds": formatMillisecondsAsSeconds,
	}
	dashTemplates = template.Must(template.New("").Funcs(funcMap).ParseGlob("templates/*.html"))
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
	var buf bytes.Buffer
	if err := dashTemplates.ExecuteTemplate(&buf, "dashboard-state.html", view); err != nil {
		return err
	}

	fmt.Fprintf(w, "event: state\n")
	for _, line := range strings.Split(buf.String(), "\n") {
		fmt.Fprintf(w, "data: %s\n", line)
	}
	_, err := fmt.Fprintf(w, "\n")
	if err != nil {
		return err
	}

	if f, ok := w.(http.Flusher); ok {
		f.Flush()
	}

	return nil
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

	for {
		select {
		case <-r.Context().Done():
			return
		case _, ok := <-sub:
			if !ok {
				return
			}

			view = buildDashboardStateView(r, stadium.DefaultManager.Snapshot(), adminKey, ownerToken)

			if err := writeDashboardSSE(w, view); err != nil {
				return
			}
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
	adminKey := strings.TrimSpace(r.FormValue("admin_key"))
	if adminKey == "" {
		adminKey = strings.TrimSpace(r.URL.Query().Get("admin_key"))
	}

	if !isAdminAuthorized(adminKey) {
		renderActionResult(w, false, "Admin authorization failed. Set BBS_DASHBOARD_ADMIN_KEY and provide a valid key.")
		return "", false
	}

	return adminKey, true
}

func requireOwnerToken(w http.ResponseWriter, r *http.Request) (string, bool) {
	ownerToken := ownerTokenFromRequest(r)
	if ownerToken == "" {
		renderActionResult(w, false, "No dashboard control token provided. Use Register Bot first.")
		return "", false
	}

	return ownerToken, true
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

	if err := stadium.DefaultManager.JoinArenaForOwner(ownerToken, arenaID, handicap); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
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

	if err := stadium.DefaultManager.LeaveArenaForOwner(ownerToken); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
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
	if err := stadium.DefaultManager.EjectOwnerSession(ownerToken, reason); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderActionResult(w, false, err.Error())
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
	mux.HandleFunc("/viewer", handleViewerPage)
	mux.HandleFunc("/viewer/live-sse", handleViewerLiveSSE)
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
	mux.HandleFunc("/", handleIndex)

	fmt.Printf("Dashboard running at http://localhost:%s\n", dashboardServerPort)
	if err := http.ListenAndServe(":"+dashboardServerPort, mux); err != nil {
		fmt.Printf("Dashboard server error: %s\n", err)
	}
}
