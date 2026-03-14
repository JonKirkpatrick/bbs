package stadium

import (
	"net"
	"time"

	"github.com/JonKirkpatrick/bbs/games" // Import your interface
)

type Session struct {
	SessionID    int
	Conn         net.Conn
	BotName      string
	Game         games.GameInstance // The link to the "Rulebook"
	PlayerID     int                // 1 or 2, assigned when match starts
	CurrentArena *Arena             // The arena this session is currently in
	Capabilities []string
	IsRegistered bool
}

type BotSettings struct {
	TimeLimit time.Duration
	Handicap  int
}
