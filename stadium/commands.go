package stadium

import (
	"fmt"
	"strings"
)

func GetHelpText(isRegistered bool) string {
	// Define the format: %-28s leaves 28 characters for the command, left-aligned.
	// You can adjust '28' if your longest command name changes.
	const format = "%-28s : %s\n"

	var sb strings.Builder
	sb.WriteString("\n--- BBS STADIUM HELP ---\n")

	if !isRegistered {
		sb.WriteString(fmt.Sprintf(format, "REGISTER <name>", "Identify yourself"))
		sb.WriteString(fmt.Sprintf(format, "QUIT", "Exit"))
	} else {
		sb.WriteString(fmt.Sprintf(format, "LIST", "View arenas"))
		sb.WriteString(fmt.Sprintf(format, "CREATE <type> <ms> <h_bool>", "Start new game"))
		sb.WriteString(fmt.Sprintf(format, "JOIN <id> <name> <handi>", "Enter game"))
		sb.WriteString(fmt.Sprintf(format, "WATCH <id>", "Spectate"))
		sb.WriteString(fmt.Sprintf(format, "MOVE <move>", "Make a move"))
		sb.WriteString(fmt.Sprintf(format, "QUIT", "Exit"))
	}

	return sb.String()
}
