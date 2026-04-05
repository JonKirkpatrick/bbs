package main

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
	"github.com/JonKirkpatrick/bbs/stadium"
)

const (
	defaultBotServerPort       = "8080"
	defaultDashboardServerPort = "3000"
)

var (
	botServerPort       = defaultBotServerPort
	dashboardServerPort = defaultDashboardServerPort
)

// WelcomeBanner is not presently being used, but I intend to find some use for it in the future.
// Originally it was being printed to the console on server and agent startup, but we have moved
// to a pure JSON protocol and this banner was no longer a good fit.
const WelcomeBanner = `
######################################################################
#__/\\\\\\\\\\\\\____/\\\\\\\\\\\\\_______/\\\\\\\\\\\_______________#
#__\/\\\/////////\\\_\/\\\/////////\\\___/\\\/////////\\\____________#
#___\/\\\_______\/\\\_\/\\\_______\/\\\__\//\\\______\///____________#
#____\/\\\\\\\\\\\\\\__\/\\\\\\\\\\\\\\____\////\\\__________________#
#_____\/\\\/////////\\\_\/\\\/////////\\\______\////\\\______________#
#______\/\\\_______\/\\\_\/\\\_______\/\\\_________\////\\\__________#
#_______\/\\\_______\/\\\_\/\\\_______\/\\\__/\\\______\//\\\________#
#________\/\\\\\\\\\\\\\/__\/\\\\\\\\\\\\\/__\///\\\\\\\\\\\/________#
#_________\/////////////____\/////////////______\///////////_________#
######################################################################

// Build-a-Bot Stadium - Perfect Information Bot Server
// Developed by Jon Kirkpatrick (github.com/JonKirkpatrick)
// For questions/comments/improvements, please reach out!
`

// Main is the main entry point for the Build-a-Bot Stadium server.
// It listens for incoming TCP connections from bots, manages their sessions,
// and routes commands to the stadium manager for processing.
func main() {
	if err := parseLaunchFlags(); err != nil {
		fmt.Printf("Invalid launch flags: %v\n", err)
		return
	}

	if err := bootstrapPersistenceAndIdentity(); err != nil {
		fmt.Printf("Failed to bootstrap persistence/identity: %v\n", err)
		return
	}

	listener, err := net.Listen("tcp", ":"+botServerPort)
	if err != nil {
		fmt.Println("Error starting stadium:", err)
		return
	}
	defer listener.Close()

	fmt.Printf("Build-a-Bot Stadium is OPEN and listening on port %s...\n", botServerPort)
	go startDashboard()

	for {
		conn, err := listener.Accept()
		if err != nil {
			continue
		}
		go handleBot(conn)
	}
}

func bootstrapPersistenceAndIdentity() error {
	paths, err := resolveRuntimePaths()
	if err != nil {
		return err
	}
	setRuntimePaths(paths)

	if strings.TrimSpace(os.Getenv(pluginDirEnv)) == "" {
		_ = os.Setenv(pluginDirEnv, paths.PluginsDir)
	}

	databasePath := paths.SQLitePath
	if err := os.MkdirAll(filepath.Dir(databasePath), 0o755); err != nil {
		return fmt.Errorf("create sqlite parent directory: %w", err)
	}

	store, err := stadium.NewSQLitePersistenceStore(databasePath)
	if err != nil {
		return err
	}
	stadium.DefaultManager.SetPersistenceStore(store)

	fmt.Printf("Runtime paths: home=%s data=%s sqlite=%s templates=%s plugins=%s\n",
		paths.ServerHome,
		paths.DataDir,
		paths.SQLitePath,
		paths.TemplatesDir,
		paths.PluginsDir,
	)

	useMockRegistry := true
	if raw := strings.TrimSpace(strings.ToLower(os.Getenv("BBS_ENABLE_MOCK_GLOBAL_REGISTRY"))); raw != "" {
		useMockRegistry = raw == "1" || raw == "true" || raw == "yes"
	}
	if useMockRegistry {
		stadium.DefaultManager.SetGlobalServerRegistrar(stadium.NewMockGlobalServerRegistrar())
	}
	outboxPublisher := stadium.OutboxPublisher(stadium.NoopOutboxPublisher{})
	outboxEndpoint := strings.TrimSpace(os.Getenv("BBS_FEDERATION_OUTBOX_URL"))
	if outboxEndpoint != "" {
		authToken := strings.TrimSpace(os.Getenv("BBS_FEDERATION_OUTBOX_TOKEN"))
		httpPublisher, err := stadium.NewHTTPOutboxPublisher(outboxEndpoint, authToken, 5*time.Second)
		if err != nil {
			return err
		}
		outboxPublisher = httpPublisher
		fmt.Printf("Outbox publisher: HTTP endpoint %s\n", outboxEndpoint)
	} else {
		fmt.Printf("Outbox publisher: no endpoint configured; using local no-op publisher\n")
	}
	stadium.DefaultManager.SetOutboxPublisher(outboxPublisher)
	stadium.DefaultManager.StartOutboxWorker(5*time.Second, 50)

	preferredName := strings.TrimSpace(os.Getenv("BBS_SERVER_DISPLAY_NAME"))
	if preferredName == "" {
		preferredName = "local-bbs"
	}

	identity, err := stadium.DefaultManager.BootstrapServerIdentity(context.Background(), preferredName, currentDashboardVersion())
	if err != nil {
		return err
	}

	fmt.Printf("Server identity ready: local=%s global=%s name=%s status=%s\n",
		identity.LocalServerID,
		identity.GlobalServerID,
		identity.AcceptedDisplayName,
		identity.RegistryStatus,
	)

	return nil
}

func parseLaunchFlags() error {
	stadium := flag.String("stadium", defaultBotServerPort, "stadium TCP port (default 8080)")
	dash := flag.String("dash", defaultDashboardServerPort, "dashboard HTTP port (default 3000)")
	flag.Parse()

	normalizedStadium, err := normalizePort(*stadium, "stadium")
	if err != nil {
		return err
	}
	normalizedDash, err := normalizePort(*dash, "dash")
	if err != nil {
		return err
	}
	if normalizedStadium == normalizedDash {
		return fmt.Errorf("--stadium and --dash cannot use the same port (%s)", normalizedStadium)
	}

	botServerPort = normalizedStadium
	dashboardServerPort = normalizedDash
	return nil
}

func normalizePort(raw, flagName string) (string, error) {
	trimmed := strings.TrimSpace(raw)
	if trimmed == "" {
		return "", fmt.Errorf("--%s is empty", flagName)
	}

	port, err := strconv.Atoi(trimmed)
	if err != nil || port < 1 || port > 65535 {
		return "", fmt.Errorf("--%s must be a valid port number (1-65535): %q", flagName, raw)
	}

	return strconv.Itoa(port), nil
}

// handleBot manages the lifecycle of a single bot connection, including registration, command processing, and cleanup on disconnect.
func handleBot(conn net.Conn) {
	// 1. Initialize a new Session for this connection
	sess := &stadium.Session{Conn: conn}

	defer func() {
		conn.Close()
		if sess.IsRegistered {
			stadium.DefaultManager.UnregisterSession(sess.SessionID)
			if sess.CurrentArena != nil {
				stadium.DefaultManager.HandlePlayerLeave(sess)
			}
		}
	}()

	// Send a JSON welcome envelope so clients can stay in JSONL mode from first byte.
	sess.SendJSON(stadium.Response{Status: "ok", Type: "welcome", Payload: map[string]string{
		"message":        "Welcome to the Build-a-Bot Stadium! Type HELP at any time for a command list.",
		"bot_port":       botServerPort,
		"dashboard_port": dashboardServerPort,
	}})
	scanner := bufio.NewScanner(conn)

	for scanner.Scan() {
		input := strings.TrimSpace(scanner.Text())
		parts := strings.Fields(input)
		if len(parts) == 0 {
			continue
		}
		command := strings.ToUpper(parts[0])

		// Enforce State Machine
		if !sess.IsRegistered && command != "REGISTER" && command != "QUIT" && command != "HELP" {
			sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Must REGISTER first"})
			continue
		}

		switch command {

		case "HELP":
			sess.SendJSON(stadium.Response{Status: "ok", Type: "help", Payload: map[string]string{"text": stadium.GetHelpText(sess.IsRegistered)}})

		case "REGISTER":
			if len(parts) < 2 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Usage: REGISTER <name> [cap1,cap2,...] [owner_token=<token>] [client_nonce=<nonce>] [client_ts=<ts>]"})
				continue
			}

			registerOptions := parseRegisterOptions(parts[2:])
			caps := registerOptions.Capabilities
			ownerToken := registerOptions.OwnerToken
			if strings.TrimSpace(ownerToken) == "" {
				issuedOwnerToken, tokenErr := stadium.NewOwnerToken()
				if tokenErr != nil {
					sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Failed to issue owner token"})
					continue
				}
				ownerToken = issuedOwnerToken
			}

			controlToken, controlTokenErr := stadium.NewControlToken()
			if controlTokenErr != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Failed to issue control token"})
				continue
			}

			serverNonce, serverNonceErr := stadium.NewControlToken()
			if serverNonceErr != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Failed to issue handshake nonce"})
				continue
			}

			result, err := stadium.DefaultManager.RegisterSession(sess, parts[1], "", "", caps, ownerToken)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: err.Error()})
				continue
			}

			dashboardHost := dashboardHostForSession(sess)
			result.OwnerToken = sess.OwnerToken
			result.ControlToken = controlToken
			result.DashboardHost = dashboardHost
			result.DashboardPort = dashboardServerPort
			result.DashboardEndpoint = net.JoinHostPort(dashboardHost, dashboardServerPort)
			result.ClientNonce = registerOptions.ClientNonce
			result.ServerNonce = serverNonce
			result.HandshakeProof = buildRegisterHandshakeProof(registerOptions.ClientNonce, registerOptions.ClientTimestamp, serverNonce, controlToken, ownerToken)

			sess.SendJSON(stadium.Response{Status: "ok", Type: "register", Payload: result})
			stadium.DefaultManager.PublishArenaList()

		case "WHOAMI":
			payload := map[string]interface{}{
				"session_id":    sess.SessionID,
				"name":          sess.BotName,
				"registered":    sess.IsRegistered,
				"current_arena": sess.CurrentArena != nil,
				"wins":          sess.Wins,
				"losses":        sess.Losses,
				"draws":         sess.Draws,
				"capabilities":  sess.Capabilities,
			}
			sess.SendJSON(stadium.Response{Status: "ok", Type: "info", Payload: payload})

		case "UPDATE":
			// Usage: UPDATE <field> <value>
			if len(parts) < 3 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: UPDATE <field> <value>"})
				continue
			}
			err := stadium.DefaultManager.UpdateSessionProfile(sess, parts[1], parts[2])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
				continue
			}
			sess.SendJSON(stadium.Response{Status: "ok", Type: "update", Payload: "Profile updated"})

		case "CREATE":
			// Usage: CREATE <type> [time_ms] [handicap_bool] [optional_args...]
			game, gameArgs, timeLimit, allowHandicap, err := parseCreateCommand(parts)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
				continue
			}

			arenaID := stadium.DefaultManager.CreateArena(game, gameArgs, timeLimit, allowHandicap)
			sess.SendJSON(stadium.Response{Status: "ok", Type: "create", Payload: strconv.Itoa(arenaID)})

		case "JOIN":
			// Usage: JOIN <arena_id> <handicap_percent>
			if len(parts) < 3 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: JOIN <arena_id> <handicap_percent>"})
				continue
			}
			arenaID, err := strconv.Atoi(parts[1])
			if err != nil || arenaID <= 0 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "arena_id must be a positive integer"})
				continue
			}
			handicap, err := strconv.Atoi(parts[2])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "handicap_percent must be an integer"})
				continue
			}

			err = stadium.DefaultManager.JoinArena(arenaID, sess, handicap)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
			}

		case "MOVE":
			if len(parts) < 2 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: MOVE <action>"})
				continue
			}

			if sess.CurrentArena == nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "No active match"})
				continue
			}

			if sess.CurrentArena.Status != "active" {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Match is not active"})
				continue
			}

			// 1. Calculate elapsed time since the last action
			elapsed := time.Since(sess.CurrentArena.LastMove)
			moveLimit := sess.CurrentArena.MoveLimitForPlayer(sess.PlayerID)
			if moveLimit <= 0 {
				moveLimit = sess.CurrentArena.TimeLimit
			}
			enforceMoveClock := games.EnforceMoveClock(sess.CurrentArena.Game)

			// 2. The Kill Switch: Check if they exceeded this player's effective move limit
			if enforceMoveClock && elapsed > moveLimit {
				arenaRef := sess.CurrentArena
				msg := fmt.Sprintf("TIMEOUT: Move took %v (Limit: %v)", elapsed.Round(time.Millisecond), moveLimit)

				// Notify everyone that the clock claimed a victim
				sess.CurrentArena.NotifyAll("error", msg)

				winnerPlayerID := 0
				if sess.PlayerID == 1 && arenaRef.Player2 != nil {
					winnerPlayerID = 2
				}
				if sess.PlayerID == 2 && arenaRef.Player1 != nil {
					winnerPlayerID = 1
				}
				_, _ = stadium.DefaultManager.FinalizeArena(arenaRef.ID, "timeout", winnerPlayerID, false)

				sess.SendJSON(stadium.Response{Status: "err", Type: "timeout", Payload: msg})
				continue
			}

			// 3. If they beat the clock, try to apply the move
			err := sess.CurrentArena.Game.ApplyMove(sess.PlayerID, parts[1])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
			} else {
				arenaRef := sess.CurrentArena
				audience := arenaRef.Audience()
				_ = stadium.DefaultManager.RecordMove(sess.CurrentArena.ID, sess, parts[1], elapsed)

				sess.SendJSON(stadium.Response{Status: "ok", Type: "move", Payload: "accepted"})
				arenaRef.NotifyAll("update", "Player "+strconv.Itoa(sess.PlayerID)+" moved to "+parts[1])

				// Pro-tip: Send the updated game state to everyone immediately
				// This was added because of earlier problems where bots would effectively
				// get stuck thinking they were in an arena after a game was already over.
				state := arenaRef.Game.GetState()
				arenaRef.NotifyAll("data", state)

				if over, winner := arenaRef.Game.IsGameOver(); over {
					winnerPlayerID, isDraw := parseWinnerResult(winner)

					if episodic, ok := arenaRef.Game.(games.EpisodicGame); ok {
						continued, episodePayload, episodeErr := episodic.AdvanceEpisode()
						if episodeErr != nil {
							arenaRef.NotifyAll("error", "Episode transition failed: "+episodeErr.Error())
						} else {
							if episodePayload == nil {
								episodePayload = make(map[string]interface{})
							}
							episodePayload["winner_player_id"] = winnerPlayerID
							episodePayload["is_draw"] = isDraw
							episodePayload["continued"] = continued
							stadium.SendJSONToSessions(audience, stadium.Response{Status: "ok", Type: "episode_end", Payload: episodePayload})

							if continued {
								arenaRef.NotifyAll("data", arenaRef.Game.GetState())
								continue
							}
						}
					}

					record, finalizeErr := stadium.DefaultManager.FinalizeArena(arenaRef.ID, "game_over", winnerPlayerID, isDraw)
					if finalizeErr == nil {
						stadium.SendJSONToSessions(audience, stadium.Response{Status: "ok", Type: "gameover", Payload: compactGameoverPayload(record)})
					}
				}
			}

		case "LIST":
			matches := stadium.DefaultManager.ListMatches()
			sess.SendJSON(stadium.Response{
				Status:  "ok",
				Type:    "list",
				Payload: matches,
			})

		case "WATCH":
			if len(parts) < 2 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: WATCH <arena_id>"})
				continue
			}
			id, err := strconv.Atoi(parts[1])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "arena_id must be a positive integer"})
				continue
			}
			err = stadium.DefaultManager.AddObserver(id, sess)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
			} else {
				sess.SendJSON(stadium.Response{Status: "ok", Type: "watch", Payload: map[string]interface{}{"arena_id": id}})
				state := sess.CurrentArena.Game.GetState()
				sess.SendJSON(stadium.Response{Status: "ok", Type: "data", Payload: state})
			}

		case "LEAVE":
			if sess.CurrentArena == nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Not currently in an arena"})
				continue
			}

			stadium.DefaultManager.HandlePlayerLeave(sess)

			sess.SendJSON(stadium.Response{Status: "ok", Type: "leave", Payload: "Left arena successfully"})

		case "QUIT":
			sess.SendJSON(stadium.Response{Status: "ok", Type: "quit", Payload: "Connection closing"})
			return // This triggers the defer conn.Close()

		default:
			sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Unknown command"})
		}
	}
}

type registerOptions struct {
	Capabilities    []string
	OwnerToken      string
	ClientNonce     string
	ClientTimestamp string
}

func parseRegisterOptions(rawParts []string) registerOptions {
	capabilities := make([]string, 0)
	options := registerOptions{}

	for _, raw := range rawParts {
		part := strings.TrimSpace(raw)
		if part == "" {
			continue
		}

		if strings.HasPrefix(strings.ToLower(part), "owner_token=") {
			options.OwnerToken = strings.TrimSpace(part[len("owner_token="):])
			continue
		}

		if strings.HasPrefix(strings.ToLower(part), "client_nonce=") {
			options.ClientNonce = strings.TrimSpace(part[len("client_nonce="):])
			continue
		}

		if strings.HasPrefix(strings.ToLower(part), "client_ts=") {
			options.ClientTimestamp = strings.TrimSpace(part[len("client_ts="):])
			continue
		}

		for _, capPart := range strings.Split(part, ",") {
			capability := strings.TrimSpace(capPart)
			if capability != "" {
				capabilities = append(capabilities, capability)
			}
		}
	}

	if len(capabilities) == 0 {
		options.Capabilities = nil
	} else {
		options.Capabilities = capabilities
	}

	return options
}

func buildRegisterHandshakeProof(clientNonce, clientTimestamp, serverNonce, controlToken, ownerToken string) string {
	proofInput := strings.Join([]string{
		strings.TrimSpace(clientNonce),
		strings.TrimSpace(clientTimestamp),
		strings.TrimSpace(serverNonce),
		strings.TrimSpace(controlToken),
		strings.TrimSpace(ownerToken),
	}, "|")
	digest := sha256.Sum256([]byte(proofInput))
	return hex.EncodeToString(digest[:])
}

func parseWinnerResult(raw string) (winnerPlayerID int, isDraw bool) {
	raw = strings.TrimSpace(strings.ToLower(raw))
	if raw == "" {
		return 0, false
	}
	if raw == "draw" {
		return 0, true
	}

	if strings.HasPrefix(raw, "player") {
		parts := strings.Fields(raw)
		if len(parts) >= 2 {
			id, err := strconv.Atoi(parts[1])
			if err == nil && (id == 1 || id == 2) {
				return id, false
			}
		}
	}

	return 0, false
}

func compactGameoverPayload(record stadium.MatchRecord) map[string]interface{} {
	return map[string]interface{}{
		"match_id":         record.MatchID,
		"arena_id":         record.ArenaID,
		"game":             record.Game,
		"terminal_status":  record.TerminalStatus,
		"end_reason":       record.EndReason,
		"winner_player_id": record.WinnerPlayerID,
		"winner_bot_id":    record.WinnerBotID,
		"winner_bot_name":  record.WinnerBotName,
		"is_draw":          record.IsDraw,
		"move_count":       record.MoveCount,
		"started_at":       record.StartedAt,
		"ended_at":         record.EndedAt,
	}
}

func dashboardHostForSession(sess *stadium.Session) string {
	if sess == nil || sess.Conn == nil {
		return "localhost"
	}

	raw := strings.TrimSpace(sess.Conn.LocalAddr().String())
	if raw == "" {
		return "localhost"
	}

	host, _, err := net.SplitHostPort(raw)
	if err != nil {
		host = raw
	}
	host = strings.TrimSpace(host)
	host = strings.Trim(host, "[]")
	if host == "" || host == "0.0.0.0" || host == "::" {
		return "localhost"
	}

	return host
}
