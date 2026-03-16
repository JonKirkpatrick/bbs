package stadium

import (
	"fmt"
	"strings"
)

// GetHelpText returns a formatted help string based on whether the bot is registered or not.
func GetHelpText(isRegistered bool) string {
	const format = "%-28s : %s\n"

	var sb strings.Builder
	sb.WriteString("\n--- BBS STADIUM HELP ---\n")

	if !isRegistered {
		fmt.Fprintf(&sb, format, "REGISTER <name> <id> <secret>", "Use id/secret or \"\" \"\" to request a new identity")
		fmt.Fprintf(&sb, format, "QUIT", "Exit")
	} else {
		fmt.Fprintf(&sb, format, "LIST", "View arenas")
		fmt.Fprintf(&sb, format, "CREATE <type> <ms> <h_bool>", "Start new game")
		fmt.Fprintf(&sb, format, "JOIN <id> <handi>", "Enter game")
		fmt.Fprintf(&sb, format, "WATCH <id>", "Spectate")
		fmt.Fprintf(&sb, format, "MOVE <move>", "Make a move")
		fmt.Fprintf(&sb, format, "QUIT", "Exit")
	}

	return sb.String()
}
