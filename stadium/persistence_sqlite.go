package stadium

import (
	"context"
	"database/sql"
	"encoding/json"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

// SQLitePersistenceStore is a SQLite-backed implementation of PersistenceStore.
type SQLitePersistenceStore struct {
	db *sql.DB
}

func NewSQLitePersistenceStore(databasePath string) (*SQLitePersistenceStore, error) {
	db, err := sql.Open("sqlite", databasePath)
	if err != nil {
		return nil, err
	}
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
		`CREATE TABLE IF NOT EXISTS bot_profiles (
			bot_id TEXT PRIMARY KEY,
			origin_server_id TEXT NOT NULL DEFAULT '',
			display_name TEXT NOT NULL,
			created_at TEXT NOT NULL,
			last_seen_at TEXT NOT NULL,
			registration_count INTEGER NOT NULL,
			games_played INTEGER NOT NULL,
			wins INTEGER NOT NULL,
			losses INTEGER NOT NULL,
			draws INTEGER NOT NULL
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
	_, err := s.db.ExecContext(ctx, `
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
	return err
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
	_, err := s.db.ExecContext(ctx, `
		INSERT INTO bot_profiles (
			bot_id, origin_server_id, display_name, created_at, last_seen_at,
			registration_count, games_played, wins, losses, draws
		)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(bot_id) DO UPDATE SET
			origin_server_id=excluded.origin_server_id,
			display_name=excluded.display_name,
			created_at=excluded.created_at,
			last_seen_at=excluded.last_seen_at,
			registration_count=excluded.registration_count,
			games_played=excluded.games_played,
			wins=excluded.wins,
			losses=excluded.losses,
			draws=excluded.draws
	`,
		profile.BotID,
		profile.OriginServerID,
		profile.DisplayName,
		profile.CreatedAt.UTC().Format(time.RFC3339Nano),
		profile.LastSeenAt.UTC().Format(time.RFC3339Nano),
		profile.RegistrationCount,
		profile.GamesPlayed,
		profile.Wins,
		profile.Losses,
		profile.Draws,
	)
	return err
}

func (s *SQLitePersistenceStore) AppendMatch(ctx context.Context, match DurableMatch, moves []DurableMatchMove) error {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

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

	return tx.Commit()
}

func (s *SQLitePersistenceStore) AppendOutboxEvent(ctx context.Context, event DurableOutboxEvent) error {
	if event.NextAttemptAt.IsZero() {
		event.NextAttemptAt = event.CreatedAt
	}
	_, err := s.db.ExecContext(ctx, `
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
	return err
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
	_, err := s.db.ExecContext(ctx, `
		UPDATE federation_outbox
		SET publish_status='published', published_at=?, last_error=''
		WHERE event_id=?
	`,
		publishedAt.UTC().Format(time.RFC3339Nano),
		eventID,
	)
	return err
}

func (s *SQLitePersistenceStore) MarkOutboxEventFailed(ctx context.Context, eventID string, nextAttemptAt time.Time, lastError string) error {
	_, err := s.db.ExecContext(ctx, `
		UPDATE federation_outbox
		SET publish_status='retry', retry_count=retry_count+1, next_attempt_at=?, last_error=?
		WHERE event_id=?
	`,
		nextAttemptAt.UTC().Format(time.RFC3339Nano),
		lastError,
		eventID,
	)
	return err
}

func (s *SQLitePersistenceStore) RecordInboxReceipt(ctx context.Context, receipt DurableInboxReceipt) error {
	_, err := s.db.ExecContext(ctx, `
		INSERT OR REPLACE INTO federation_inbox_dedupe (source_server_id, source_event_id, processed_at)
		VALUES (?, ?, ?)
	`,
		receipt.SourceServerID,
		receipt.SourceEventID,
		receipt.ProcessedAt.UTC().Format(time.RFC3339Nano),
	)
	return err
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
