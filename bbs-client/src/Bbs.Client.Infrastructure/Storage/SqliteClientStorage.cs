using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Storage;
using Bbs.Client.Infrastructure.Paths;
using Microsoft.Data.Sqlite;

namespace Bbs.Client.Infrastructure.Storage;

public sealed class SqliteClientStorage : IClientStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int CurrentSchemaVersion = 2;
    private readonly string _dbPath;

    public SqliteClientStorage(string? dbPath = null)
    {
        _dbPath = dbPath ?? StoragePaths.GetDatabaseFilePath();
    }

    public string DatabasePath => _dbPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var hasSchemaVersionTable = await TableExistsAsync(connection, "schema_version", cancellationToken);
        if (!hasSchemaVersionTable)
        {
            if (await HasAnyUserTablesAsync(connection, cancellationToken))
            {
                throw new InvalidOperationException("Legacy client database format is unsupported. Delete the database and reinitialize.");
            }

            await CreateCurrentSchemaAsync(connection, cancellationToken);
            return;
        }

        var version = await ReadSchemaVersionAsync(connection, cancellationToken);
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported database schema version {version}. Expected {CurrentSchemaVersion}.");
        }

        await EnsureCurrentTablesExistAsync(connection, cancellationToken);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await TableExistsAsync(connection, "schema_version", cancellationToken))
        {
            return 0;
        }

        return await ReadSchemaVersionAsync(connection, cancellationToken);
    }

    public async Task<ClientIdentity?> GetClientIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_id, display_name, created_at_utc FROM client_identity LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClientIdentity(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)));
    }

    public async Task SaveClientIdentityAsync(ClientIdentity identity, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO client_identity(client_id, display_name, created_at_utc)
            VALUES ($id, $display_name, $created_at_utc)
            ON CONFLICT(client_id)
            DO UPDATE SET
                display_name = excluded.display_name,
                created_at_utc = excluded.created_at_utc
            """;
        command.Parameters.AddWithValue("$id", identity.ClientId);
        command.Parameters.AddWithValue("$display_name", identity.DisplayName);
        command.Parameters.AddWithValue("$created_at_utc", identity.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BotProfile>> ListBotProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bot_id, name, launch_path, launch_args_json, metadata_json, created_at_utc, updated_at_utc FROM bot_profiles ORDER BY created_at_utc";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<BotProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var args = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOptions) ?? new List<string>();
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), JsonOptions) ?? new Dictionary<string, string>();
            results.Add(BotProfile.Create(
                botId: reader.GetString(0),
                name: reader.GetString(1),
                launchPath: reader.GetString(2),
                launchArgs: args,
                metadata: metadata,
                createdAtUtc: DateTimeOffset.Parse(reader.GetString(5)),
                updatedAtUtc: DateTimeOffset.Parse(reader.GetString(6))));
        }

        return results;
    }

    public async Task UpsertBotProfileAsync(BotProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bot_profiles(bot_id, name, launch_path, launch_args_json, metadata_json, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $launch_path, $launch_args_json, $metadata_json, $created_at_utc, $updated_at_utc)
            ON CONFLICT(bot_id)
            DO UPDATE SET
                name = excluded.name,
                launch_path = excluded.launch_path,
                launch_args_json = excluded.launch_args_json,
                metadata_json = excluded.metadata_json,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$id", profile.BotId);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$launch_path", profile.LaunchPath);
        command.Parameters.AddWithValue("$launch_args_json", JsonSerializer.Serialize(profile.LaunchArgs, JsonOptions));
        command.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(profile.Metadata, JsonOptions));
        command.Parameters.AddWithValue("$created_at_utc", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", profile.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BotServerCredential?> GetBotServerCredentialAsync(string clientBotId, string serverId, string? serverGlobalId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(serverGlobalId))
        {
            await using var byGlobal = connection.CreateCommand();
            byGlobal.CommandText = """
                SELECT client_bot_id, server_id, server_global_id, server_bot_id, server_bot_secret, created_at_utc, updated_at_utc
                FROM bot_server_credentials
                WHERE client_bot_id = $client_bot_id AND server_global_id = $server_global_id
                LIMIT 1
                """;
            byGlobal.Parameters.AddWithValue("$client_bot_id", clientBotId);
            byGlobal.Parameters.AddWithValue("$server_global_id", serverGlobalId);

            await using var byGlobalReader = await byGlobal.ExecuteReaderAsync(cancellationToken);
            if (await byGlobalReader.ReadAsync(cancellationToken))
            {
                return BotServerCredential.Create(
                    clientBotId: byGlobalReader.GetString(0),
                    serverId: byGlobalReader.GetString(1),
                    serverGlobalId: byGlobalReader.IsDBNull(2) ? null : byGlobalReader.GetString(2),
                    serverBotId: byGlobalReader.GetString(3),
                    serverBotSecret: byGlobalReader.GetString(4),
                    createdAtUtc: DateTimeOffset.Parse(byGlobalReader.GetString(5)),
                    updatedAtUtc: DateTimeOffset.Parse(byGlobalReader.GetString(6)));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client_bot_id, server_id, server_global_id, server_bot_id, server_bot_secret, created_at_utc, updated_at_utc
            FROM bot_server_credentials
            WHERE client_bot_id = $client_bot_id AND server_id = $server_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$client_bot_id", clientBotId);
        command.Parameters.AddWithValue("$server_id", serverId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return BotServerCredential.Create(
            clientBotId: reader.GetString(0),
            serverId: reader.GetString(1),
            serverGlobalId: reader.IsDBNull(2) ? null : reader.GetString(2),
            serverBotId: reader.GetString(3),
            serverBotSecret: reader.GetString(4),
            createdAtUtc: DateTimeOffset.Parse(reader.GetString(5)),
            updatedAtUtc: DateTimeOffset.Parse(reader.GetString(6)));
    }

    public async Task UpsertBotServerCredentialAsync(BotServerCredential credential, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bot_server_credentials(
                client_bot_id,
                server_id,
                server_global_id,
                server_bot_id,
                server_bot_secret,
                created_at_utc,
                updated_at_utc)
            VALUES (
                $client_bot_id,
                $server_id,
                $server_global_id,
                $server_bot_id,
                $server_bot_secret,
                $created_at_utc,
                $updated_at_utc)
            ON CONFLICT(client_bot_id, server_id)
            DO UPDATE SET
                server_global_id = excluded.server_global_id,
                server_bot_id = excluded.server_bot_id,
                server_bot_secret = excluded.server_bot_secret,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$client_bot_id", credential.ClientBotId);
        command.Parameters.AddWithValue("$server_id", credential.ServerId);
        command.Parameters.AddWithValue("$server_global_id", (object?)credential.ServerGlobalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$server_bot_id", credential.ServerBotId);
        command.Parameters.AddWithValue("$server_bot_secret", credential.ServerBotSecret);
        command.Parameters.AddWithValue("$created_at_utc", credential.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", credential.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KnownServer>> ListKnownServersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_id, name, host, port, use_tls, metadata_json, created_at_utc, updated_at_utc FROM known_servers ORDER BY created_at_utc";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<KnownServer>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5), JsonOptions) ?? new Dictionary<string, string>();
            results.Add(KnownServer.Create(
                serverId: reader.GetString(0),
                name: reader.GetString(1),
                host: reader.GetString(2),
                port: reader.GetInt32(3),
                useTls: reader.GetInt32(4) == 1,
                metadata: metadata,
                createdAtUtc: DateTimeOffset.Parse(reader.GetString(6)),
                updatedAtUtc: DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public async Task UpsertKnownServerAsync(KnownServer server, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO known_servers(server_id, name, host, port, use_tls, metadata_json, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $host, $port, $use_tls, $metadata_json, $created_at_utc, $updated_at_utc)
            ON CONFLICT(server_id)
            DO UPDATE SET
                name = excluded.name,
                host = excluded.host,
                port = excluded.port,
                use_tls = excluded.use_tls,
                metadata_json = excluded.metadata_json,
                created_at_utc = excluded.created_at_utc,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$id", server.ServerId);
        command.Parameters.AddWithValue("$name", server.Name);
        command.Parameters.AddWithValue("$host", server.Host);
        command.Parameters.AddWithValue("$port", server.Port);
        command.Parameters.AddWithValue("$use_tls", server.UseTls ? 1 : 0);
        command.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(server.Metadata, JsonOptions));
        command.Parameters.AddWithValue("$created_at_utc", server.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", server.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ServerPluginCache?> GetServerPluginCacheAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_id, plugins_json, cached_at_utc FROM server_plugin_cache WHERE server_id = $server_id";
        command.Parameters.AddWithValue("$server_id", serverId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var plugins = JsonSerializer.Deserialize<List<PluginDescriptor>>(reader.GetString(1), JsonOptions) ?? new List<PluginDescriptor>();
        return ServerPluginCache.Create(
            serverId: reader.GetString(0),
            plugins: plugins,
            cachedAtUtc: DateTimeOffset.Parse(reader.GetString(2)));
    }

    public async Task UpsertServerPluginCacheAsync(ServerPluginCache cache, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_plugin_cache(server_id, plugins_json, cached_at_utc)
            VALUES ($server_id, $plugins_json, $cached_at_utc)
            ON CONFLICT(server_id)
            DO UPDATE SET
                plugins_json = excluded.plugins_json,
                cached_at_utc = excluded.cached_at_utc
            """;
        command.Parameters.AddWithValue("$server_id", cache.ServerId);
        command.Parameters.AddWithValue("$plugins_json", JsonSerializer.Serialize(cache.Plugins, JsonOptions));
        command.Parameters.AddWithValue("$cached_at_utc", cache.CachedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AgentRuntimeState?> GetAgentRuntimeStateAsync(string botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bot_id, lifecycle_state, is_armed, last_error_code, updated_at_utc FROM agent_runtime_state WHERE bot_id = $bot_id";
        command.Parameters.AddWithValue("$bot_id", botId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AgentRuntimeState(
            BotId: reader.GetString(0),
            LifecycleState: (AgentLifecycleState)reader.GetInt32(1),
            IsArmed: reader.GetInt32(2) == 1,
            LastErrorCode: reader.IsDBNull(3) ? null : reader.GetString(3),
            UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(4)));
    }

    public async Task UpsertAgentRuntimeStateAsync(AgentRuntimeState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_runtime_state(bot_id, lifecycle_state, is_armed, last_error_code, updated_at_utc)
            VALUES ($bot_id, $lifecycle_state, $is_armed, $last_error_code, $updated_at_utc)
            ON CONFLICT(bot_id)
            DO UPDATE SET
                lifecycle_state = excluded.lifecycle_state,
                is_armed = excluded.is_armed,
                last_error_code = excluded.last_error_code,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$bot_id", state.BotId);
        command.Parameters.AddWithValue("$lifecycle_state", (int)state.LifecycleState);
        command.Parameters.AddWithValue("$is_armed", state.IsArmed ? 1 : 0);
        command.Parameters.AddWithValue("$last_error_code", (object?)state.LastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_at_utc", state.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    private static async Task<bool> HasAnyUserTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            LIMIT 1
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version WHERE id = 1";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task CreateCurrentSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    version INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS client_identity (
                    client_id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS bot_profiles (
                    bot_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    launch_path TEXT NOT NULL,
                    launch_args_json TEXT NOT NULL,
                    metadata_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS known_servers (
                    server_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    host TEXT NOT NULL,
                    port INTEGER NOT NULL,
                    use_tls INTEGER NOT NULL,
                    metadata_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS server_plugin_cache (
                    server_id TEXT PRIMARY KEY,
                    plugins_json TEXT NOT NULL,
                    cached_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS agent_runtime_state (
                    bot_id TEXT PRIMARY KEY,
                    lifecycle_state INTEGER NOT NULL,
                    is_armed INTEGER NOT NULL,
                    last_error_code TEXT,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS bot_server_credentials (
                    client_bot_id TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    server_global_id TEXT,
                    server_bot_id TEXT NOT NULL,
                    server_bot_secret TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (client_bot_id, server_id)
                );

                CREATE INDEX IF NOT EXISTS idx_bot_server_credentials_global
                ON bot_server_credentials(client_bot_id, server_global_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateVersion = connection.CreateCommand())
        {
            updateVersion.Transaction = transaction;
            updateVersion.CommandText = """
                INSERT INTO schema_version(id, version, updated_at_utc)
                VALUES (1, $version, $updated_at_utc)
                ON CONFLICT(id)
                DO UPDATE SET
                    version = excluded.version,
                    updated_at_utc = $updated_at_utc
                """;
            updateVersion.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            updateVersion.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O"));
            await updateVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task EnsureCurrentTablesExistAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS client_identity (
                client_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bot_profiles (
                bot_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                launch_path TEXT NOT NULL,
                launch_args_json TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS known_servers (
                server_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                use_tls INTEGER NOT NULL,
                metadata_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS server_plugin_cache (
                server_id TEXT PRIMARY KEY,
                plugins_json TEXT NOT NULL,
                cached_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agent_runtime_state (
                bot_id TEXT PRIMARY KEY,
                lifecycle_state INTEGER NOT NULL,
                is_armed INTEGER NOT NULL,
                last_error_code TEXT,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bot_server_credentials (
                client_bot_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                server_global_id TEXT,
                server_bot_id TEXT NOT NULL,
                server_bot_secret TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (client_bot_id, server_id)
            );

            CREATE INDEX IF NOT EXISTS idx_bot_server_credentials_global
            ON bot_server_credentials(client_bot_id, server_global_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
