package main

import (
	"bufio"
	"flag"
	"fmt"
	"net"
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

// WelcomeBanner is the ASCII art displayed to bots upon connection, along with a brief introduction to the Build-a-Bot Stadium.
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

	// Display the banner
	conn.Write([]byte(WelcomeBanner))
	conn.Write([]byte("\nWelcome to the Build-a-Bot Stadium!"))
	conn.Write([]byte("\nType HELP at any time for a command list.\n\n"))
	scanner := bufio.NewScanner(conn)

	for scanner.Scan() {
		input := strings.TrimSpace(scanner.Text())
		parts := strings.Split(input, " ")
		if len(parts) == 0 {
			continue
		}
		command := parts[0]

		// Enforce State Machine
		if !sess.IsRegistered && command != "REGISTER" && command != "QUIT" && command != "HELP" {
			sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Must REGISTER first"})
			continue
		}

		switch command {

		case "HELP":
			sess.Conn.Write([]byte(stadium.GetHelpText(sess.IsRegistered) + "\n"))

		case "REGISTER":
			if len(parts) < 4 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Usage: REGISTER <name> <bot_id_or_\"\"> <bot_secret_or_\"\"> [cap1,cap2,...] [owner_token=<token>]"})
				continue
			}

			caps, ownerToken := parseRegisterOptions(parts[4:])

			result, err := stadium.DefaultManager.RegisterSession(sess, parts[1], parts[2], parts[3], caps, ownerToken)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: err.Error()})
				continue
			}

			sess.SendJSON(stadium.Response{Status: "ok", Type: "register", Payload: result})

		case "WHOAMI":
			payload := map[string]interface{}{
				"session_id":    sess.SessionID,
				"bot_id":        sess.BotID,
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
				conn.Write([]byte("ERR: No active match\n"))
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
				sess.CurrentArena.NotifyAll("update", "Player "+strconv.Itoa(sess.PlayerID)+" moved to "+parts[1])

				// Pro-tip: Send the updated game state to everyone immediately
				// This was added because of earlier problems where bots would effectively
				// get stuck thinking they were in an arena after a game was already over.
				state := sess.CurrentArena.Game.GetState()
				sess.CurrentArena.NotifyAll("data", state)

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

			var sb strings.Builder
			sb.WriteString("CURRENT_ARENAS:\n")
			for _, m := range matches {
				fmt.Fprintf(&sb, "%d: [%s] %s vs %s\n", m.ID, m.Game, m.P1Name, m.P2Name)
			}
			sess.SendJSON(stadium.Response{
				Status:  "ok",
				Type:    "list",
				Payload: sb.String(),
			})

		case "WATCH":
			if len(parts) < 2 {
				conn.Write([]byte("ERR: Usage: WATCH <match_id>\n"))
				continue
			}
			id, err := strconv.Atoi(parts[1])
			if err != nil {
				conn.Write([]byte("ERR: Invalid Match ID\n"))
				continue
			}
			err = stadium.DefaultManager.AddObserver(id, sess)
			if err != nil {
				conn.Write([]byte("ERR: " + err.Error() + "\n"))
			} else {
				conn.Write([]byte("OK: Watching match " + parts[1] + "\n"))
				state := sess.CurrentArena.Game.GetState()
				conn.Write([]byte("DATA: \n" + state + "\n"))
			}

		case "LEAVE":
			if sess.CurrentArena == nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Not currently in an arena"})
				continue
			}

			stadium.DefaultManager.HandlePlayerLeave(sess)

			sess.SendJSON(stadium.Response{Status: "ok", Type: "leave", Payload: "Left arena successfully"})

		case "QUIT":
			conn.Write([]byte("BBS_STADIUM: Connection closed. Press ENTER to return to your prompt.\n"))
			return // This triggers the defer conn.Close()

		default:
			conn.Write([]byte("ERR: Unknown command\n"))
		}
	}
}

func parseRegisterOptions(rawParts []string) ([]string, string) {
	capabilities := make([]string, 0)
	ownerToken := ""

	for _, raw := range rawParts {
		part := strings.TrimSpace(raw)
		if part == "" {
			continue
		}

		if strings.HasPrefix(strings.ToLower(part), "owner_token=") {
			ownerToken = strings.TrimSpace(part[len("owner_token="):])
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
		capabilities = nil
	}

	return capabilities, ownerToken
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
