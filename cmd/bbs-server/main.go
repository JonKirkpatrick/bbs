package main

import (
	"bufio"
	"fmt"
	"net"
	"strconv"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
	"github.com/JonKirkpatrick/bbs/stadium"
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
	listener, err := net.Listen("tcp", ":8080")
	if err != nil {
		fmt.Println("Error starting stadium:", err)
		return
	}
	defer listener.Close()

	fmt.Println("Build-a-Bot Stadium is OPEN and listening on port 8080...")
	go startDashboard()

	for {
		conn, err := listener.Accept()
		if err != nil {
			continue
		}
		go handleBot(conn)
	}
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

	// A quick pause adds to the "connection" feel.  I may remove it or make it configurable later.
	time.Sleep(100 * time.Millisecond)
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
			if len(parts) < 2 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: "Usage: REGISTER <name> [cap1,cap2,...]"})
				continue
			}

			// Parse capabilities if provided (e.g., "connect4,tictactoe")
			var caps []string
			if len(parts) > 2 {
				caps = strings.Split(parts[2], ",")
			}

			err := stadium.DefaultManager.RegisterSession(sess, parts[1], caps)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: err.Error()})
				continue
			}

			msg := fmt.Sprintf("Registered as %s with capabilities: %v", sess.BotName, sess.Capabilities)
			sess.SendJSON(stadium.Response{Status: "ok", Type: "register", Payload: msg})

		case "WHOAMI":
			// Usage: WHOAMI
			payload := fmt.Sprintf("ID: %d, Name: %s, Registered: %t, Arena: %v",
				sess.SessionID, sess.BotName, sess.IsRegistered, (sess.CurrentArena != nil))
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
			// Usage: CREATE <type> <time_ms> <handicap_bool> [optional_args...]
			if len(parts) < 4 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: CREATE <type> <time> <handicap> [args...]"})
				continue
			}

			gameType := parts[1]
			// The slice [4:] contains everything after the time and handicap flags
			game, err := games.GetGame(gameType, parts[4:])

			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
				continue
			}
			timeLimit, _ := strconv.Atoi(parts[2])
			allowHandicap := parts[3] == "true"

			arenaID := stadium.DefaultManager.CreateArena(game, time.Duration(timeLimit)*time.Millisecond, allowHandicap)
			sess.SendJSON(stadium.Response{Status: "ok", Type: "create", Payload: strconv.Itoa(arenaID)})

		case "JOIN":
			// Usage: JOIN <arena_id> <bot_name> <handicap_value>
			if len(parts) < 4 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: JOIN <arena_id> <name> <handicap>"})
				continue
			}
			arenaID, _ := strconv.Atoi(parts[1])
			sess.BotName = parts[2]
			handicap, _ := strconv.Atoi(parts[3])

			err := stadium.DefaultManager.JoinArena(arenaID, sess, handicap)
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
			} else {
				sess.SendJSON(stadium.Response{Status: "ok", Type: "join", Payload: "Joined arena " + parts[1]})
			}

		case "MOVE":
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

			// 2. The Kill Switch: Check if they exceeded the arena's TimeLimit
			if elapsed > sess.CurrentArena.TimeLimit {
				msg := fmt.Sprintf("TIMEOUT: Move took %v (Limit: %v)", elapsed.Round(time.Millisecond), sess.CurrentArena.TimeLimit)

				// Notify everyone that the clock claimed a victim
				sess.CurrentArena.NotifyAll("error", msg)

				// Mark the arena as completed to prevent further moves
				sess.CurrentArena.Status = "completed"
				stadium.DefaultManager.PublishArenaList()

				sess.SendJSON(stadium.Response{Status: "err", Type: "timeout", Payload: msg})
				continue
			}

			// 3. If they beat the clock, try to apply the move
			err := sess.CurrentArena.Game.ApplyMove(sess.PlayerID, parts[1])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: err.Error()})
			} else {
				// 4. Success! Update the 'LastMove' timestamp for the NEXT player
				sess.CurrentArena.LastMove = time.Now()

				sess.SendJSON(stadium.Response{Status: "ok", Type: "move", Payload: "accepted"})
				sess.CurrentArena.NotifyAll("update", "Player "+strconv.Itoa(sess.PlayerID)+" moved to "+parts[1])

				// Pro-tip: Send the updated game state to everyone immediately
				state := sess.CurrentArena.Game.GetState()
				sess.CurrentArena.NotifyAll("data", state)
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
				// Optional: Send current game state immediately upon joining
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
