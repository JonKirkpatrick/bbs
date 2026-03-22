package stadium

import (
	"context"
	"encoding/json"
	"strings"
	"time"
)

// SetPersistenceStore swaps the manager's persistence backend.
func (m *Manager) SetPersistenceStore(store PersistenceStore) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if store == nil {
		m.persistence = NewInMemoryPersistenceStore()
		return
	}
	m.persistence = store
}

// SetGlobalServerRegistrar sets the registrar used to obtain a global server identity.
func (m *Manager) SetGlobalServerRegistrar(registrar GlobalServerRegistrar) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.registrar = registrar
}

// ServerIdentitySnapshot returns a copy of the active server identity if one has been bootstrapped.
func (m *Manager) ServerIdentitySnapshot() (ServerIdentity, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.hasServerID {
		return ServerIdentity{}, false
	}
	return m.serverIdentity, true
}

// DurableServerIdentity returns the persisted server identity directly from the storage backend.
func (m *Manager) DurableServerIdentity(ctx context.Context) (ServerIdentity, bool, error) {
	m.mu.Lock()
	store := m.persistence
	m.mu.Unlock()
	if store == nil {
		return ServerIdentity{}, false, nil
	}
	return store.LoadServerIdentity(ctx)
}

// RecentDurableMatchesForBot returns recent durable matches involving the provided bot ID.
func (m *Manager) RecentDurableMatchesForBot(ctx context.Context, botID string, limit int) ([]DurableMatch, error) {
	m.mu.Lock()
	store := m.persistence
	m.mu.Unlock()
	if store == nil {
		return nil, nil
	}
	return store.ListRecentMatchesForBot(ctx, botID, limit)
}

// PendingOutboxEvents returns pending or retryable outbox events eligible at or before now.
func (m *Manager) PendingOutboxEvents(ctx context.Context, limit int, now time.Time) ([]DurableOutboxEvent, error) {
	m.mu.Lock()
	store := m.persistence
	m.mu.Unlock()
	if store == nil {
		return nil, nil
	}
	return store.ListPendingOutboxEvents(ctx, limit, now)
}

// RecordInboundFederationEvent records a remote event receipt and reports whether it was already seen.
func (m *Manager) RecordInboundFederationEvent(ctx context.Context, sourceServerID, sourceEventID string, processedAt time.Time) (bool, error) {
	sourceServerID = strings.TrimSpace(sourceServerID)
	sourceEventID = strings.TrimSpace(sourceEventID)
	if sourceServerID == "" || sourceEventID == "" {
		return false, nil
	}

	m.mu.Lock()
	store := m.persistence
	m.mu.Unlock()
	if store == nil {
		return false, nil
	}

	seen, err := store.HasInboxReceipt(ctx, sourceServerID, sourceEventID)
	if err != nil {
		return false, err
	}
	if seen {
		return true, nil
	}

	if err := store.RecordInboxReceipt(ctx, DurableInboxReceipt{
		SourceServerID: sourceServerID,
		SourceEventID:  sourceEventID,
		ProcessedAt:    processedAt.UTC(),
	}); err != nil {
		return false, err
	}

	return false, nil
}

// BootstrapServerIdentity loads or creates a durable local identity and optionally registers it globally.
func (m *Manager) BootstrapServerIdentity(ctx context.Context, preferredDisplayName, softwareVersion string) (ServerIdentity, error) {
	m.mu.Lock()
	store := m.persistence
	registrar := m.registrar
	cached := m.serverIdentity
	hasCached := m.hasServerID
	m.mu.Unlock()

	if store == nil {
		store = NewInMemoryPersistenceStore()
		m.SetPersistenceStore(store)
	}

	identity := cached
	if !hasCached {
		loaded, found, err := store.LoadServerIdentity(ctx)
		if err != nil {
			return ServerIdentity{}, err
		}
		if found {
			identity = loaded
		} else {
			created, err := BuildNewLocalServerIdentity(preferredDisplayName)
			if err != nil {
				return ServerIdentity{}, err
			}
			identity = created
			if err := store.SaveServerIdentity(ctx, identity); err != nil {
				return ServerIdentity{}, err
			}
		}
	}

	if registrar != nil {
		res, err := registrar.RegisterServer(ctx, ServerRegistrationRequest{
			LocalServerID:        identity.LocalServerID,
			PublicKeyFingerprint: identity.PublicKeyFingerprint,
			PreferredDisplayName: preferredDisplayName,
			SoftwareVersion:      softwareVersion,
		})
		if err != nil {
			return ServerIdentity{}, err
		}
		identity.GlobalServerID = res.GlobalServerID
		identity.AcceptedDisplayName = res.AcceptedDisplayName
		identity.RegistryStatus = res.Status
		identity.LastRegistrationAt = res.IssuedAt
		if err := store.SaveServerIdentity(ctx, identity); err != nil {
			return ServerIdentity{}, err
		}
	}

	m.mu.Lock()
	m.serverIdentity = identity
	m.hasServerID = true
	m.mu.Unlock()
	return identity, nil
}

func (m *Manager) persistBotProfileLocked(profile *BotProfile) {
	if profile == nil || m.persistence == nil {
		return
	}
	origin := ""
	if m.hasServerID {
		origin = m.serverIdentity.LocalServerID
	}
	_ = m.persistence.UpsertBotProfile(context.Background(), DurableBotProfile{
		BotID:             profile.BotID,
		OriginServerID:    origin,
		DisplayName:       profile.DisplayName,
		CreatedAt:         profile.CreatedAt.UTC(),
		LastSeenAt:        profile.LastSeenAt.UTC(),
		RegistrationCount: profile.RegistrationCount,
		GamesPlayed:       profile.GamesPlayed,
		Wins:              profile.Wins,
		Losses:            profile.Losses,
		Draws:             profile.Draws,
	})
}

func (m *Manager) persistMatchRecord(record MatchRecord) {
	m.mu.Lock()
	store := m.persistence
	origin := ""
	if m.hasServerID {
		origin = m.serverIdentity.LocalServerID
	}
	m.mu.Unlock()

	if store == nil {
		return
	}

	startedAt, err := time.Parse(time.RFC3339Nano, record.StartedAt)
	if err != nil {
		startedAt = time.Now().UTC()
	}
	endedAt, err := time.Parse(time.RFC3339Nano, record.EndedAt)
	if err != nil {
		endedAt = time.Now().UTC()
	}

	moves := make([]DurableMatchMove, 0, len(record.Moves))
	for _, mv := range record.Moves {
		occurredAt, parseErr := time.Parse(time.RFC3339Nano, mv.OccurredAt)
		if parseErr != nil {
			occurredAt = endedAt
		}
		moves = append(moves, DurableMatchMove{
			MatchID:        record.MatchID,
			OriginServerID: origin,
			Sequence:       mv.Number,
			PlayerID:       mv.PlayerID,
			SessionID:      mv.SessionID,
			BotID:          mv.BotID,
			BotName:        mv.BotName,
			Move:           mv.Move,
			ElapsedMS:      mv.ElapsedMS,
			OccurredAt:     occurredAt.UTC(),
		})
	}

	_ = store.AppendMatch(context.Background(), DurableMatch{
		MatchID:        record.MatchID,
		ArenaID:        record.ArenaID,
		OriginServerID: origin,
		Game:           record.Game,
		GameArgs:       append([]string(nil), record.GameArgs...),
		TerminalStatus: record.TerminalStatus,
		EndReason:      record.EndReason,
		WinnerPlayerID: record.WinnerPlayerID,
		WinnerBotID:    record.WinnerBotID,
		WinnerBotName:  record.WinnerBotName,
		IsDraw:         record.IsDraw,
		StartedAt:      startedAt.UTC(),
		EndedAt:        endedAt.UTC(),
		FinalGameState: record.FinalGameState,
	}, moves)

	payload, err := json.Marshal(record)
	if err != nil {
		return
	}
	eventID, err := newToken("evt", 12)
	if err != nil {
		return
	}
	_ = store.AppendOutboxEvent(context.Background(), DurableOutboxEvent{
		EventID:        eventID,
		OriginServerID: origin,
		EventType:      "match_finalized",
		PayloadJSON:    string(payload),
		CreatedAt:      time.Now().UTC(),
		NextAttemptAt:  time.Now().UTC(),
		PublishStatus:  "pending",
		RetryCount:     0,
	})
}
