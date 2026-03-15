package main

import (
	"bytes"
	"crypto/subtle"
	"fmt"
	"html/template"
	"net/http"
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
	AdminConfigured bool
	IsAdmin         bool
	AdminKey        string
}

type dashboardStateView struct {
	Snapshot        stadium.ManagerSnapshot
	IsAdmin         bool
	AdminConfigured bool
	AdminKey        string
}

func initDashboard() {
	files, _ := filepath.Glob("templates/*.html")
	fmt.Printf("Found %d dashboard templates: %v\n", len(files), files)
	dashTemplates = template.Must(template.ParseGlob("templates/*.html"))
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

func renderAdminResult(w http.ResponseWriter, ok bool, message string) {
	class := "admin-result error"
	if ok {
		class = "admin-result success"
	}
	fmt.Fprintf(w, `<div class="%s">%s</div>`, class, template.HTMLEscapeString(message))
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
	isAdmin := isAdminAuthorized(adminKey)

	sub := stadium.DefaultManager.Subscribe()
	defer stadium.DefaultManager.Unsubscribe(sub)

	view := dashboardStateView{
		Snapshot:        stadium.DefaultManager.Snapshot(),
		IsAdmin:         isAdmin,
		AdminConfigured: dashboardAdminKey() != "",
		AdminKey:        adminKey,
	}

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

			view.Snapshot = stadium.DefaultManager.Snapshot()

			if err := writeDashboardSSE(w, view); err != nil {
				return
			}
		}
	}
}

func handleIndex(w http.ResponseWriter, r *http.Request) {
	adminKey := r.URL.Query().Get("admin_key")
	data := dashboardIndexData{
		AdminConfigured: dashboardAdminKey() != "",
		IsAdmin:         isAdminAuthorized(adminKey),
		AdminKey:        adminKey,
	}
	dashTemplates.ExecuteTemplate(w, "index.html", data)
}

func requireAdmin(w http.ResponseWriter, r *http.Request) (string, bool) {
	adminKey := strings.TrimSpace(r.FormValue("admin_key"))
	if adminKey == "" {
		adminKey = strings.TrimSpace(r.URL.Query().Get("admin_key"))
	}

	if !isAdminAuthorized(adminKey) {
		renderAdminResult(w, false, "Admin authorization failed. Set BBS_DASHBOARD_ADMIN_KEY and provide a valid key.")
		return "", false
	}

	return adminKey, true
}

func handleAdminEjectBot(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderAdminResult(w, false, "Use POST for admin actions.")
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
		renderAdminResult(w, false, "Invalid session ID.")
		return
	}

	reason := strings.TrimSpace(r.FormValue("reason"))
	if err := stadium.DefaultManager.EjectSession(sessionID, reason); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderAdminResult(w, false, err.Error())
		return
	}

	renderAdminResult(w, true, fmt.Sprintf("Session %d ejected.", sessionID))
}

func handleAdminCreateArena(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		renderAdminResult(w, false, "Use POST for admin actions.")
		return
	}

	_, ok := requireAdmin(w, r)
	if !ok {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	gameType := strings.TrimSpace(r.FormValue("game"))
	if gameType == "" {
		w.WriteHeader(http.StatusBadRequest)
		renderAdminResult(w, false, "Game type is required.")
		return
	}

	timeLimitMS, err := strconv.Atoi(strings.TrimSpace(r.FormValue("time_ms")))
	if err != nil || timeLimitMS <= 0 {
		w.WriteHeader(http.StatusBadRequest)
		renderAdminResult(w, false, "Time limit must be a positive integer in milliseconds.")
		return
	}

	allowHandicap, err := strconv.ParseBool(strings.TrimSpace(r.FormValue("allow_handicap")))
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderAdminResult(w, false, "allow_handicap must be true or false.")
		return
	}

	args := parseGameArgs(r.FormValue("game_args"))
	game, err := games.GetGame(gameType, args)
	if err != nil {
		w.WriteHeader(http.StatusBadRequest)
		renderAdminResult(w, false, err.Error())
		return
	}

	arenaID := stadium.DefaultManager.CreateArena(game, time.Duration(timeLimitMS)*time.Millisecond, allowHandicap)
	renderAdminResult(w, true, fmt.Sprintf("Created arena %d (%s).", arenaID, gameType))
}

func startDashboard() {
	initDashboard()

	mux := http.NewServeMux()
	mux.HandleFunc("/dashboard-sse", handleDashboardSSE)
	mux.HandleFunc("/admin/eject-bot", handleAdminEjectBot)
	mux.HandleFunc("/admin/create-arena", handleAdminCreateArena)
	mux.HandleFunc("/", handleIndex)

	fmt.Println("Dashboard running at http://localhost:3000")
	if err := http.ListenAndServe(":3000", mux); err != nil {
		fmt.Printf("Dashboard server error: %s\n", err)
	}
}
