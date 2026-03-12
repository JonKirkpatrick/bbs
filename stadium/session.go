package stadium

import (
	"net"

	"github.com/JonKirkpatrick/bbs/games" // Import your interface
)

type Session struct {
	Conn         net.Conn
	BotName      string
	Game         games.GameInstance // The link to the "Rulebook"
	PlayerID     int                // 1 or 2, assigned when match starts
	CurrentMatch *Match             // The match this session is currently in
}
