package stadium

import (
	"context"
	"sort"
	"sync"
	"time"
)

// DurableBotProfile captures the subset of bot profile fields intended for durable storage.
type DurableBotProfile struct {
	BotID             string
	OriginServerID    string
	DisplayName       string
	CreatedAt         time.Time
	LastSeenAt        time.Time
	RegistrationCount int
	GamesPlayed       int
	Wins              int
	Losses            int
	Draws             int
}

// DurableMatch captures terminal match metadata intended for durable storage.
type DurableMatch struct {
	MatchID        int
	ArenaID        int
	OriginServerID string
	Game           string
	GameArgs       []string
	TerminalStatus string
	EndReason      string
	WinnerPlayerID int
	WinnerBotID    string
	WinnerBotName  string
	IsDraw         bool
	StartedAt      time.Time
	EndedAt        time.Time
	FinalGameState string
}

// DurableMatchMove captures one durable move/event for replay and audit.
type DurableMatchMove struct {
	MatchID        int
	OriginServerID string
	Sequence       int
	PlayerID       int
	SessionID      int
	BotID          string
	BotName        string
	Move           string
	ElapsedMS      int64
	OccurredAt     time.Time
}

// DurableOutboxEvent stores outbound federation events for reliable delivery.
type DurableOutboxEvent struct {
	EventID        string
	OriginServerID string
	EventType      string
	PayloadJSON    string
	CreatedAt      time.Time
	PublishedAt    time.Time
	NextAttemptAt  time.Time
	PublishStatus  string
	RetryCount     int
	LastError      string
}

// DurableInboxReceipt stores processed remote events for idempotency.
type DurableInboxReceipt struct {
	SourceServerID string
	SourceEventID  string
	ProcessedAt    time.Time
}

// PersistenceStore defines the durable data operations needed for staged SQLite adoption.
type PersistenceStore interface {
	SaveServerIdentity(ctx context.Context, identity ServerIdentity) error
	LoadServerIdentity(ctx context.Context) (ServerIdentity, bool, error)

	UpsertBotProfile(ctx context.Context, profile DurableBotProfile) error
	AppendMatch(ctx context.Context, match DurableMatch, moves []DurableMatchMove) error

	AppendOutboxEvent(ctx context.Context, event DurableOutboxEvent) error
	ListPendingOutboxEvents(ctx context.Context, limit int, now time.Time) ([]DurableOutboxEvent, error)
	MarkOutboxEventPublished(ctx context.Context, eventID string, publishedAt time.Time) error
	MarkOutboxEventFailed(ctx context.Context, eventID string, nextAttemptAt time.Time, lastError string) error

	RecordInboxReceipt(ctx context.Context, receipt DurableInboxReceipt) error
	HasInboxReceipt(ctx context.Context, sourceServerID, sourceEventID string) (bool, error)

	ListRecentMatchesForBot(ctx context.Context, botID string, limit int) ([]DurableMatch, error)
}

// InMemoryPersistenceStore is a no-op in-process implementation used before SQLite wiring.
type InMemoryPersistenceStore struct {
	mu             sync.Mutex
	serverIdentity ServerIdentity
	hasIdentity    bool

	botProfiles map[string]DurableBotProfile
	matches     []DurableMatch
	matchMoves  map[int][]DurableMatchMove
	outbox      map[string]DurableOutboxEvent
	inbox       map[string]time.Time
}

func NewInMemoryPersistenceStore() *InMemoryPersistenceStore {
	return &InMemoryPersistenceStore{
		botProfiles: make(map[string]DurableBotProfile),
		matches:     make([]DurableMatch, 0),
		matchMoves:  make(map[int][]DurableMatchMove),
		outbox:      make(map[string]DurableOutboxEvent),
		inbox:       make(map[string]time.Time),
	}
}

func (s *InMemoryPersistenceStore) SaveServerIdentity(_ context.Context, identity ServerIdentity) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.serverIdentity = identity
	s.hasIdentity = true
	return nil
}

func (s *InMemoryPersistenceStore) LoadServerIdentity(_ context.Context) (ServerIdentity, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.hasIdentity {
		return ServerIdentity{}, false, nil
	}
	return s.serverIdentity, true, nil
}

func (s *InMemoryPersistenceStore) UpsertBotProfile(_ context.Context, profile DurableBotProfile) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.botProfiles[profile.BotID] = profile
	return nil
}

func (s *InMemoryPersistenceStore) AppendMatch(_ context.Context, match DurableMatch, moves []DurableMatchMove) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.matches = append(s.matches, match)
	cloned := make([]DurableMatchMove, len(moves))
	copy(cloned, moves)
	s.matchMoves[match.MatchID] = cloned
	return nil
}

func (s *InMemoryPersistenceStore) AppendOutboxEvent(_ context.Context, event DurableOutboxEvent) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if event.NextAttemptAt.IsZero() {
		event.NextAttemptAt = event.CreatedAt
	}
	s.outbox[event.EventID] = event
	return nil
}

func (s *InMemoryPersistenceStore) ListPendingOutboxEvents(_ context.Context, limit int, now time.Time) ([]DurableOutboxEvent, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if limit <= 0 {
		limit = 50
	}
	pending := make([]DurableOutboxEvent, 0, len(s.outbox))
	for _, event := range s.outbox {
		if event.PublishStatus != "pending" && event.PublishStatus != "retry" {
			continue
		}
		if !event.NextAttemptAt.IsZero() && event.NextAttemptAt.After(now) {
			continue
		}
		pending = append(pending, event)
	}
	sort.Slice(pending, func(i, j int) bool {
		return pending[i].CreatedAt.Before(pending[j].CreatedAt)
	})
	if len(pending) > limit {
		pending = pending[:limit]
	}
	return pending, nil
}

func (s *InMemoryPersistenceStore) MarkOutboxEventPublished(_ context.Context, eventID string, publishedAt time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	event, ok := s.outbox[eventID]
	if !ok {
		return nil
	}
	event.PublishStatus = "published"
	event.PublishedAt = publishedAt
	event.LastError = ""
	s.outbox[eventID] = event
	return nil
}

func (s *InMemoryPersistenceStore) MarkOutboxEventFailed(_ context.Context, eventID string, nextAttemptAt time.Time, lastError string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	event, ok := s.outbox[eventID]
	if !ok {
		return nil
	}
	event.PublishStatus = "retry"
	event.RetryCount++
	event.NextAttemptAt = nextAttemptAt
	event.LastError = lastError
	s.outbox[eventID] = event
	return nil
}

func (s *InMemoryPersistenceStore) RecordInboxReceipt(_ context.Context, receipt DurableInboxReceipt) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.inbox[inboxKey(receipt.SourceServerID, receipt.SourceEventID)] = receipt.ProcessedAt
	return nil
}

func (s *InMemoryPersistenceStore) HasInboxReceipt(_ context.Context, sourceServerID, sourceEventID string) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, ok := s.inbox[inboxKey(sourceServerID, sourceEventID)]
	return ok, nil
}

func (s *InMemoryPersistenceStore) ListRecentMatchesForBot(_ context.Context, botID string, limit int) ([]DurableMatch, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if limit <= 0 {
		limit = 25
	}
	results := make([]DurableMatch, 0, limit)
	for i := len(s.matches) - 1; i >= 0; i-- {
		match := s.matches[i]
		moves, ok := s.matchMoves[match.MatchID]
		if !ok {
			continue
		}
		seen := false
		for _, mv := range moves {
			if mv.BotID == botID {
				seen = true
				break
			}
		}
		if !seen {
			continue
		}
		results = append(results, match)
		if len(results) >= limit {
			break
		}
	}
	return results, nil
}

func inboxKey(sourceServerID, sourceEventID string) string {
	return sourceServerID + "::" + sourceEventID
}
