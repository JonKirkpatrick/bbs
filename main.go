package main

import (
	"bufio"
	"fmt"
	"net"
	"strconv"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/stadium"
)

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

func main() {
	listener, err := net.Listen("tcp", ":8080")
	if err != nil {
		fmt.Println("Error starting stadium:", err)
		return
	}
	defer listener.Close()

	fmt.Println("Build-a-Bot Stadium is OPEN and listening on port 8080...")

	for {
		conn, err := listener.Accept()
		if err != nil {
			continue
		}
		go handleBot(conn)
	}
}

func handleBot(conn net.Conn) {
	defer conn.Close()

	// 1. Initialize a new Session for this connection
	sess := &stadium.Session{Conn: conn}

	// Display the banner
	conn.Write([]byte(WelcomeBanner))

	// A quick pause adds to the "connection" feel
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
			// Usage: REGISTER <bot_name>
			if len(parts) < 2 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: REGISTER <name>"})
				continue
			}

			err := stadium.DefaultManager.RegisterSession(sess, parts[1])
			if err != nil {
				sess.SendJSON(stadium.Response{Status: "err", Type: "auth", Payload: err.Error()})
			} else {
				sess.SendJSON(stadium.Response{Status: "ok", Type: "register", Payload: fmt.Sprintf("Registered as %s (ID: %d)", sess.BotName, sess.SessionID)})
			}

		case "CREATE":
			// Usage: CREATE <game_type> <time_limit_ms> <allow_handicap_bool>
			if len(parts) < 4 {
				sess.SendJSON(stadium.Response{Status: "err", Type: "error", Payload: "Usage: CREATE <game> <time_ms> <handicap_bool>"})
				continue
			}
			gameType := parts[1]
			timeLimit, _ := strconv.Atoi(parts[2])
			allowHandicap := parts[3] == "true"

			arenaID := stadium.DefaultManager.CreateArena(gameType, time.Duration(timeLimit)*time.Millisecond, allowHandicap)
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

			// 1. Calculate elapsed time since the last action
			elapsed := time.Since(sess.CurrentArena.LastMove)

			// 2. The Kill Switch: Check if they exceeded the arena's TimeLimit
			if elapsed > sess.CurrentArena.TimeLimit {
				msg := fmt.Sprintf("TIMEOUT: Move took %v (Limit: %v)", elapsed.Round(time.Millisecond), sess.CurrentArena.TimeLimit)

				// Notify everyone that the clock claimed a victim
				sess.CurrentArena.NotifyAll("error", msg)

				// Mark the arena as finished to prevent further moves
				sess.CurrentArena.Status = "finished"

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
			conn.Write([]byte(stadium.DefaultManager.ListMatches() + "\n"))

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

		case "QUIT":
			// Send a polite goodbye message before closing
			conn.Write([]byte("BBS_STADIUM: Connection closed. Press ENTER to return to your prompt.\n"))
			return // This triggers the defer conn.Close()

		default:
			conn.Write([]byte("ERR: Unknown command\n"))
		}
	}
}
