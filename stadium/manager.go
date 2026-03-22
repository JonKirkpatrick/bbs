package stadium

import "sync"

// Manager is the central coordinator for all arenas and sessions in the Build-a-Bot Stadium.
// It handles arena creation, player matchmaking, session management, and periodic cleanup of inactive arenas.
type Manager struct {
	mu              sync.Mutex
	Arenas          map[int]*Arena
	ActiveSessions  map[int]*Session
	BotProfiles     map[string]*BotProfile
	MatchHistory    []MatchRecord
	persistence     PersistenceStore
	registrar       GlobalServerRegistrar
	outboxPublisher OutboxPublisher
	outboxStopCh    chan struct{}
	serverIdentity  ServerIdentity
	hasServerID     bool
	subscribers     map[chan StadiumEvent]struct{} // For dashboard updates
	nextArenaID     int
	nextSessionID   int
	nextMatchID     int
}

// DefaultManager is the global instance of the Manager that handles all arenas and sessions in the stadium.
var DefaultManager = &Manager{}

// init initializes the DefaultManager and starts the watchdog goroutine for arena cleanup.
func init() {
	DefaultManager = &Manager{
		Arenas:         make(map[int]*Arena),
		nextArenaID:    1,
		ActiveSessions: make(map[int]*Session),
		BotProfiles:    make(map[string]*BotProfile),
		MatchHistory:   make([]MatchRecord, 0),
		persistence:    NewInMemoryPersistenceStore(),
		subscribers:    make(map[chan StadiumEvent]struct{}),
		nextSessionID:  1,
		nextMatchID:    1,
	}
	DefaultManager.StartWatchdog()
}
