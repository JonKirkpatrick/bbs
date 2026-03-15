package stadium

import (
	"net"
	"sync"
	"time"
)

type Session struct {
	SessionID    int
	Conn         net.Conn
	mu           sync.Mutex // Protects concurrent writes to the connection
	BotName      string
	PlayerID     int // 1 or 2
	CurrentArena *Arena
	Capabilities []string
	IsRegistered bool
}

// BotSettings represents the configuration options for a bot when creating or joining an arena, such as time limits and handicap settings.
type BotSettings struct {
	TimeLimit time.Duration // Time limit per move
	Handicap  int           // Handicap value, if applicable
}
