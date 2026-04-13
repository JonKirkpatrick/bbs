package stadium

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

// SQLitePersistenceStore is a SQLite-backed implementation of PersistenceStore.
type SQLitePersistenceStore struct {
	db *sql.DB
}

const (
	sqliteBusyRetryAttempts = 3
	sqliteBusyRetryDelay    = 200 * time.Millisecond
)

func NewSQLitePersistenceStore(databasePath string) (*SQLitePersistenceStore, error) {
	db, err := sql.Open("sqlite", databasePath+"?_pragma=busy_timeout(5000)&_pragma=journal_mode=WAL")
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	store := &SQLitePersistenceStore{db: db}
	if err := store.migrate(context.Background()); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

func (s *SQLitePersistenceStore) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	return s.db.Close()
}

func (s *SQLitePersistenceStore) migrate(ctx context.Context) error {
	schema := []string{
		`CREATE TABLE IF NOT EXISTS server_identity (
			local_server_id TEXT PRIMARY KEY,
			global_server_id TEXT NOT NULL DEFAULT '',
			public_key_fingerprint TEXT NOT NULL,
			public_key_hex TEXT NOT NULL,
			private_key_hex TEXT NOT NULL,
			preferred_display_name TEXT NOT NULL DEFAULT '',
			accepted_display_name TEXT NOT NULL DEFAULT '',
			registry_status TEXT NOT NULL DEFAULT 'pending',
			created_at TEXT NOT NULL,
			last_registration_at TEXT NOT NULL DEFAULT ''
		);`,
		`CREATE TABLE IF NOT EXISTS matches (
			match_id INTEGER PRIMARY KEY,
			arena_id INTEGER NOT NULL,
			origin_server_id TEXT NOT NULL DEFAULT '',
			game TEXT NOT NULL,
			game_args_json TEXT NOT NULL,
			terminal_status TEXT NOT NULL,
			end_reason TEXT NOT NULL,
			winner_player_id INTEGER NOT NULL,
			winner_bot_id TEXT NOT NULL,
			winner_bot_name TEXT NOT NULL,
			is_draw INTEGER NOT NULL,
			started_at TEXT NOT NULL,
			ended_at TEXT NOT NULL,
			final_game_state TEXT NOT NULL
		);`,
		`CREATE TABLE IF NOT EXISTS match_moves (
			match_id INTEGER NOT NULL,
			origin_server_id TEXT NOT NULL DEFAULT '',
			sequence INTEGER NOT NULL,
			player_id INTEGER NOT NULL,
			session_id INTEGER NOT NULL,
			bot_id TEXT NOT NULL,
			bot_name TEXT NOT NULL,
			move TEXT NOT NULL,
			elapsed_ms INTEGER NOT NULL,
			occurred_at TEXT NOT NULL,
			PRIMARY KEY (match_id, sequence)
		);`,
		`CREATE TABLE IF NOT EXISTS federation_outbox (
			event_id TEXT PRIMARY KEY,
			origin_server_id TEXT NOT NULL DEFAULT '',
			event_type TEXT NOT NULL,
			payload_json TEXT NOT NULL,
			created_at TEXT NOT NULL,
			published_at TEXT NOT NULL DEFAULT '',
			next_attempt_at TEXT NOT NULL DEFAULT '',
			publish_status TEXT NOT NULL,
			retry_count INTEGER NOT NULL,
			last_error TEXT NOT NULL DEFAULT ''
		);`,
		`CREATE INDEX IF NOT EXISTS idx_federation_outbox_pending ON federation_outbox (publish_status, next_attempt_at, created_at);`,
		`CREATE TABLE IF NOT EXISTS federation_inbox_dedupe (
			source_server_id TEXT NOT NULL,
			source_event_id TEXT NOT NULL,
			processed_at TEXT NOT NULL,
			PRIMARY KEY (source_server_id, source_event_id)
		);`,
	}
	for _, stmt := range schema {
		if _, err := s.db.ExecContext(ctx, stmt); err != nil {
			return err
		}
	}
	if err := s.ensureColumn(ctx, "federation_outbox", "next_attempt_at", "TEXT NOT NULL DEFAULT ''"); err != nil {
		return err
	}
	if err := s.ensureColumn(ctx, "federation_outbox", "last_error", "TEXT NOT NULL DEFAULT ''"); err != nil {
		return err
	}
	return nil
}

func (s *SQLitePersistenceStore) ensureColumn(ctx context.Context, tableName, columnName, columnDDL string) error {
	_, err := s.db.ExecContext(ctx, "ALTER TABLE "+tableName+" ADD COLUMN "+columnName+" "+columnDDL)
	if err == nil {
		return nil
	}
	if strings.Contains(strings.ToLower(err.Error()), "duplicate column") {
		return nil
	}
	return err
}

func (s *SQLitePersistenceStore) SaveServerIdentity(ctx context.Context, identity ServerIdentity) error {
	return s.execWrite(ctx, `
		INSERT INTO server_identity (
			local_server_id, global_server_id, public_key_fingerprint, public_key_hex, private_key_hex,
			preferred_display_name, accepted_display_name, registry_status, created_at, last_registration_at
		)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(local_server_id) DO UPDATE SET
			global_server_id=excluded.global_server_id,
			public_key_fingerprint=excluded.public_key_fingerprint,
			public_key_hex=excluded.public_key_hex,
			private_key_hex=excluded.private_key_hex,
			preferred_display_name=excluded.preferred_display_name,
			accepted_display_name=excluded.accepted_display_name,
			registry_status=excluded.registry_status,
			created_at=excluded.created_at,
			last_registration_at=excluded.last_registration_at
	`,
		identity.LocalServerID,
		identity.GlobalServerID,
		identity.PublicKeyFingerprint,
		identity.PublicKeyHex,
		identity.PrivateKeyHex,
		identity.PreferredDisplayName,
		identity.AcceptedDisplayName,
		identity.RegistryStatus,
		identity.CreatedAt.UTC().Format(time.RFC3339Nano),
		identity.LastRegistrationAt.UTC().Format(time.RFC3339Nano),
	)
}

func (s *SQLitePersistenceStore) LoadServerIdentity(ctx context.Context) (ServerIdentity, bool, error) {
	row := s.db.QueryRowContext(ctx, `
		SELECT local_server_id, global_server_id, public_key_fingerprint, public_key_hex, private_key_hex,
			preferred_display_name, accepted_display_name, registry_status, created_at, last_registration_at
		FROM server_identity
		LIMIT 1
	`)

	var id ServerIdentity
	var createdAtRaw string
	var lastRegistrationRaw string
	err := row.Scan(
		&id.LocalServerID,
		&id.GlobalServerID,
		&id.PublicKeyFingerprint,
		&id.PublicKeyHex,
		&id.PrivateKeyHex,
		&id.PreferredDisplayName,
		&id.AcceptedDisplayName,
		&id.RegistryStatus,
		&createdAtRaw,
		&lastRegistrationRaw,
	)
	if err == sql.ErrNoRows {
		return ServerIdentity{}, false, nil
	}
	if err != nil {
		return ServerIdentity{}, false, err
	}
	if parsed, parseErr := time.Parse(time.RFC3339Nano, createdAtRaw); parseErr == nil {
		id.CreatedAt = parsed.UTC()
	}
	if parsed, parseErr := time.Parse(time.RFC3339Nano, lastRegistrationRaw); parseErr == nil {
		id.LastRegistrationAt = parsed.UTC()
	}
	return id, true, nil
}

func (s *SQLitePersistenceStore) UpsertBotProfile(ctx context.Context, profile DurableBotProfile) error {
	_ = ctx
	_ = profile
	return nil
}

func (s *SQLitePersistenceStore) AppendMatch(ctx context.Context, match DurableMatch, moves []DurableMatchMove) error {
	return s.withWriteTx(ctx, func(tx *sql.Tx) error {
		argsJSON, err := json.Marshal(match.GameArgs)
		if err != nil {
			return err
		}

		_, err = tx.ExecContext(ctx, `
			INSERT OR REPLACE INTO matches (
				match_id, arena_id, origin_server_id, game, game_args_json, terminal_status, end_reason,
				winner_player_id, winner_bot_id, winner_bot_name, is_draw, started_at, ended_at, final_game_state
			)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		`,
			match.MatchID,
			match.ArenaID,
			match.OriginServerID,
			match.Game,
			string(argsJSON),
			match.TerminalStatus,
			match.EndReason,
			match.WinnerPlayerID,
			match.WinnerBotID,
			match.WinnerBotName,
			boolToInt(match.IsDraw),
			match.StartedAt.UTC().Format(time.RFC3339Nano),
			match.EndedAt.UTC().Format(time.RFC3339Nano),
			match.FinalGameState,
		)
		if err != nil {
			return err
		}

		for _, mv := range moves {
			_, err = tx.ExecContext(ctx, `
				INSERT OR REPLACE INTO match_moves (
					match_id, origin_server_id, sequence, player_id, session_id,
					bot_id, bot_name, move, elapsed_ms, occurred_at
				)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
			`,
				mv.MatchID,
				mv.OriginServerID,
				mv.Sequence,
				mv.PlayerID,
				mv.SessionID,
				mv.BotID,
				mv.BotName,
				mv.Move,
				mv.ElapsedMS,
				mv.OccurredAt.UTC().Format(time.RFC3339Nano),
			)
			if err != nil {
				return err
			}
		}

		return nil
	})
}

func (s *SQLitePersistenceStore) AppendOutboxEvent(ctx context.Context, event DurableOutboxEvent) error {
	if event.NextAttemptAt.IsZero() {
		event.NextAttemptAt = event.CreatedAt
	}
	return s.execWrite(ctx, `
		INSERT OR REPLACE INTO federation_outbox (
			event_id, origin_server_id, event_type, payload_json, created_at,
			published_at, next_attempt_at, publish_status, retry_count, last_error
		)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
	`,
		event.EventID,
		event.OriginServerID,
		event.EventType,
		event.PayloadJSON,
		event.CreatedAt.UTC().Format(time.RFC3339Nano),
		event.PublishedAt.UTC().Format(time.RFC3339Nano),
		event.NextAttemptAt.UTC().Format(time.RFC3339Nano),
		event.PublishStatus,
		event.RetryCount,
		event.LastError,
	)
}

func (s *SQLitePersistenceStore) ListPendingOutboxEvents(ctx context.Context, limit int, now time.Time) ([]DurableOutboxEvent, error) {
	if limit <= 0 {
		limit = 50
	}
	rows, err := s.db.QueryContext(ctx, `
		SELECT event_id, origin_server_id, event_type, payload_json, created_at, published_at,
			next_attempt_at, publish_status, retry_count, last_error
		FROM federation_outbox
		WHERE (publish_status='pending' OR publish_status='retry')
			AND (next_attempt_at='' OR next_attempt_at <= ?)
		ORDER BY created_at ASC
		LIMIT ?
	`, now.UTC().Format(time.RFC3339Nano), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	events := make([]DurableOutboxEvent, 0)
	for rows.Next() {
		var event DurableOutboxEvent
		var createdRaw string
		var publishedRaw string
		var nextAttemptRaw string
		if err := rows.Scan(
			&event.EventID,
			&event.OriginServerID,
			&event.EventType,
			&event.PayloadJSON,
			&createdRaw,
			&publishedRaw,
			&nextAttemptRaw,
			&event.PublishStatus,
			&event.RetryCount,
			&event.LastError,
		); err != nil {
			return nil, err
		}
		if parsed, parseErr := time.Parse(time.RFC3339Nano, createdRaw); parseErr == nil {
			event.CreatedAt = parsed.UTC()
		}
		if parsed, parseErr := time.Parse(time.RFC3339Nano, publishedRaw); parseErr == nil {
			event.PublishedAt = parsed.UTC()
		}
		if parsed, parseErr := time.Parse(time.RFC3339Nano, nextAttemptRaw); parseErr == nil {
			event.NextAttemptAt = parsed.UTC()
		}
		events = append(events, event)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return events, nil
}

func (s *SQLitePersistenceStore) MarkOutboxEventPublished(ctx context.Context, eventID string, publishedAt time.Time) error {
	return s.execWrite(ctx, `
		UPDATE federation_outbox
		SET publish_status='published', published_at=?, last_error=''
		WHERE event_id=?
	`,
		publishedAt.UTC().Format(time.RFC3339Nano),
		eventID,
	)
}

func (s *SQLitePersistenceStore) MarkOutboxEventFailed(ctx context.Context, eventID string, nextAttemptAt time.Time, lastError string) error {
	return s.execWrite(ctx, `
		UPDATE federation_outbox
		SET publish_status='retry', retry_count=retry_count+1, next_attempt_at=?, last_error=?
		WHERE event_id=?
	`,
		nextAttemptAt.UTC().Format(time.RFC3339Nano),
		lastError,
		eventID,
	)
}

func (s *SQLitePersistenceStore) RecordInboxReceipt(ctx context.Context, receipt DurableInboxReceipt) error {
	return s.execWrite(ctx, `
		INSERT OR REPLACE INTO federation_inbox_dedupe (source_server_id, source_event_id, processed_at)
		VALUES (?, ?, ?)
	`,
		receipt.SourceServerID,
		receipt.SourceEventID,
		receipt.ProcessedAt.UTC().Format(time.RFC3339Nano),
	)
}

func (s *SQLitePersistenceStore) execWrite(ctx context.Context, query string, args ...any) error {
	return s.retryOnBusy(ctx, func() error {
		_, err := s.db.ExecContext(ctx, query, args...)
		return err
	})
}

func (s *SQLitePersistenceStore) withWriteTx(ctx context.Context, fn func(*sql.Tx) error) error {
	return s.retryOnBusy(ctx, func() error {
		tx, err := s.db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}

		if err := fn(tx); err != nil {
			_ = tx.Rollback()
			return err
		}

		if err := tx.Commit(); err != nil {
			_ = tx.Rollback()
			return err
		}
		return nil
	})
}

func (s *SQLitePersistenceStore) retryOnBusy(ctx context.Context, op func() error) error {
	var lastErr error
	for attempt := 0; attempt < sqliteBusyRetryAttempts; attempt++ {
		if ctx.Err() != nil {
			return ctx.Err()
		}

		err := op()
		if err == nil {
			return nil
		}
		if !isSQLiteBusyError(err) {
			return err
		}
		lastErr = err

		if attempt == sqliteBusyRetryAttempts-1 {
			break
		}

		timer := time.NewTimer(sqliteBusyRetryDelay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
	}

	if lastErr != nil {
		return lastErr
	}
	return errors.New("sqlite write retry failed")
}

func isSQLiteBusyError(err error) bool {
	if err == nil {
		return false
	}
	lower := strings.ToLower(err.Error())
	return strings.Contains(lower, "database is locked") || strings.Contains(lower, "database is busy")
}

func (s *SQLitePersistenceStore) HasInboxReceipt(ctx context.Context, sourceServerID, sourceEventID string) (bool, error) {
	row := s.db.QueryRowContext(ctx, `
		SELECT 1
		FROM federation_inbox_dedupe
		WHERE source_server_id=? AND source_event_id=?
		LIMIT 1
	`, sourceServerID, sourceEventID)
	var one int
	err := row.Scan(&one)
	if err == sql.ErrNoRows {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	return true, nil
}

func (s *SQLitePersistenceStore) ListRecentMatchesForBot(ctx context.Context, botID string, limit int) ([]DurableMatch, error) {
	if limit <= 0 {
		limit = 25
	}
	rows, err := s.db.QueryContext(ctx, `
		SELECT DISTINCT m.match_id, m.arena_id, m.origin_server_id, m.game, m.game_args_json,
			m.terminal_status, m.end_reason, m.winner_player_id, m.winner_bot_id, m.winner_bot_name,
			m.is_draw, m.started_at, m.ended_at, m.final_game_state
		FROM matches m
		INNER JOIN match_moves mv ON mv.match_id = m.match_id
		WHERE mv.bot_id = ?
		ORDER BY m.ended_at DESC
		LIMIT ?
	`, botID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	matches := make([]DurableMatch, 0)
	for rows.Next() {
		var match DurableMatch
		var gameArgsJSON string
		var isDrawInt int
		var startedRaw string
		var endedRaw string
		if err := rows.Scan(
			&match.MatchID,
			&match.ArenaID,
			&match.OriginServerID,
			&match.Game,
			&gameArgsJSON,
			&match.TerminalStatus,
			&match.EndReason,
			&match.WinnerPlayerID,
			&match.WinnerBotID,
			&match.WinnerBotName,
			&isDrawInt,
			&startedRaw,
			&endedRaw,
			&match.FinalGameState,
		); err != nil {
			return nil, err
		}
		_ = json.Unmarshal([]byte(gameArgsJSON), &match.GameArgs)
		match.IsDraw = isDrawInt == 1
		if parsed, parseErr := time.Parse(time.RFC3339Nano, startedRaw); parseErr == nil {
			match.StartedAt = parsed.UTC()
		}
		if parsed, parseErr := time.Parse(time.RFC3339Nano, endedRaw); parseErr == nil {
			match.EndedAt = parsed.UTC()
		}
		matches = append(matches, match)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return matches, nil
}

func boolToInt(v bool) int {
	if v {
		return 1
	}
	return 0
}
