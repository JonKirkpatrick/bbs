package stadium

import (
	"io"
	"net"
	"sync"
	"testing"
	"time"
)

type testAddr string

func (a testAddr) Network() string { return "test" }
func (a testAddr) String() string  { return string(a) }

type testConn struct {
	mu     sync.Mutex
	writes [][]byte
}

func (c *testConn) Read(_ []byte) (int, error) { return 0, io.EOF }

func (c *testConn) Write(p []byte) (int, error) {
	c.mu.Lock()
	copyBuf := append([]byte(nil), p...)
	c.writes = append(c.writes, copyBuf)
	c.mu.Unlock()
	return len(p), nil
}

func (c *testConn) Close() error { return nil }

func (c *testConn) LocalAddr() net.Addr  { return testAddr("local") }
func (c *testConn) RemoteAddr() net.Addr { return testAddr("remote") }

func (c *testConn) SetDeadline(_ time.Time) error      { return nil }
func (c *testConn) SetReadDeadline(_ time.Time) error  { return nil }
func (c *testConn) SetWriteDeadline(_ time.Time) error { return nil }

func (c *testConn) writeCount() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.writes)
}

type testGame struct {
	name             string
	state            string
	requiredPlayers  int
	enforceMoveClock bool
	supportsHandicap bool
}

func (g testGame) GetName() string { return g.name }

func (g testGame) GetState() string {
	if g.state == "" {
		return "{}"
	}
	return g.state
}

func (g testGame) ValidateMove(playerID int, move string) error { return nil }
func (g testGame) ApplyMove(playerID int, move string) error    { return nil }
func (g testGame) IsGameOver() (bool, string)                   { return false, "" }

func (g testGame) RequiredPlayers() int { return g.requiredPlayers }

func (g testGame) EnforceMoveClock() bool { return g.enforceMoveClock }

func (g testGame) SupportsHandicap() bool { return g.supportsHandicap }

func newTestManager() *Manager {
	return &Manager{
		Arenas:         make(map[int]*Arena),
		ActiveSessions: make(map[int]*Session),
		BotProfiles:    make(map[string]*BotProfile),
		MatchHistory:   make([]MatchRecord, 0),
		persistence:    NewInMemoryPersistenceStore(),
		subscribers:    make(map[chan StadiumEvent]struct{}),
		nextArenaID:    1,
		nextSessionID:  1,
		nextMatchID:    1,
	}
}

func newRegisteredSession(t *testing.T, m *Manager, name string) (*Session, RegistrationResult) {
	t.Helper()
	s := &Session{Conn: &testConn{}}
	result, err := m.RegisterSession(s, name, "", "", []string{"any"}, "")
	if err != nil {
		t.Fatalf("RegisterSession failed: %v", err)
	}
	return s, result
}
