package main

import (
	"bufio"
	"fmt"
	"net"
	"strings"

	"github.com/JonKirkpatrick/bbs/stadium"
)

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
	sess := &stadium.Session{
		Conn: conn,
	}

	scanner := bufio.NewScanner(conn)
	conn.Write([]byte("BBS_WELCOME: Please send JOIN <bot_name> <game_type>\n"))

	for scanner.Scan() {
		input := strings.TrimSpace(scanner.Text())
		parts := strings.Split(input, " ")
		if len(parts) == 0 {
			continue
		}
		command := parts[0]

		switch command {
		case "JOIN":
			if len(parts) >= 3 {
				sess.BotName = parts[1]
				// 2. Hand the session off to the Manager
				// The Manager handles the Registry lookup and pairing!
				stadium.DefaultManager.AddToWaitingRoom(sess)
			} else {
				conn.Write([]byte("ERR: Usage: JOIN <name> <game>\n"))
			}

		case "MOVE":
			if sess.CurrentMatch == nil {
				conn.Write([]byte("ERR: No active match\n"))
				continue
			}

			err := sess.CurrentMatch.Game.ApplyMove(sess.PlayerID, parts[1])
			if err != nil {
				conn.Write([]byte("ERR: " + err.Error() + "\n"))
			} else {
				// 1. Confirm to the mover
				conn.Write([]byte("OK: Move accepted.\n"))

				// 2. Alert the opponent
				updateMsg := fmt.Sprintf("Opponent moved in column %s. Your turn!", parts[1])
				sess.CurrentMatch.NotifyOpponent(sess.PlayerID, updateMsg)

				// 3. (Optional) Check if game is over
				over, winner := sess.CurrentMatch.Game.IsGameOver()
				if over {
					broadcast := fmt.Sprintf("GAMEOVER: Winner is %s\n", winner)
					sess.CurrentMatch.Player1.Conn.Write([]byte(broadcast))
					sess.CurrentMatch.Player2.Conn.Write([]byte(broadcast))
				}
			}

		case "QUIT":
			return
		default:
			conn.Write([]byte("ERR: Unknown command\n"))
		}
	}
}
