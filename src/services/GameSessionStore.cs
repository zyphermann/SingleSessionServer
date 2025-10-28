using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

sealed record GameSessionRecord(
    Guid GameSessionId,
    Guid GameStateId,
    Guid PlayerId,
    int SessionIndex,
    JsonElement SessionState,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt);

sealed class GameSessionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public GameSessionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<GameSessionRecord> CreateAsync(
        Guid playerId,
        Guid gameStateId,
        JsonElement sessionState,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var nextIndex = await GetNextSessionIndexAsync(conn, tx, gameStateId, cancellationToken);

        const string insertSql =
            """
            INSERT INTO game_sessions (
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state)
            VALUES (@id, @gameStateId, @playerId, @sessionIndex, @sessionState)
            RETURNING
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = insertSql;

        var newId = Guid.NewGuid();
        cmd.Parameters.AddWithValue("id", newId);
        cmd.Parameters.AddWithValue("gameStateId", gameStateId);
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("sessionIndex", nextIndex);
        cmd.Parameters.Add("sessionState", NpgsqlDbType.Jsonb).Value = SerializeState(sessionState);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Failed to create game session.");

        var result = ReadSession(reader);
        await reader.CloseAsync();
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<GameSessionRecord?> TryGetAsync(
        Guid playerId,
        Guid gameSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at
            FROM game_sessions
            WHERE player_id = @playerId
              AND game_session_id = @id;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("id", gameSessionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    public async Task<GameSessionRecord?> TryGetByIndexAsync(
        Guid playerId,
        Guid gameStateId,
        int sessionIndex,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at
            FROM game_sessions
            WHERE player_id = @playerId
              AND game_state_id = @gameStateId
              AND session_index = @sessionIndex;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("gameStateId", gameStateId);
        cmd.Parameters.AddWithValue("sessionIndex", sessionIndex);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    public async Task<GameSessionRecord?> TryGetLatestAsync(
        Guid playerId,
        Guid gameStateId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at
            FROM game_sessions
            WHERE player_id = @playerId
              AND game_state_id = @gameStateId
            ORDER BY session_index DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("gameStateId", gameStateId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    public async Task<GameSessionRecord?> UpdateStateAsync(
        Guid playerId,
        Guid gameStateId,
        int sessionIndex,
        JsonElement sessionState,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE game_sessions
            SET session_state = @sessionState,
                updated_at = NOW()
            WHERE player_id = @playerId
              AND game_state_id = @gameStateId
              AND session_index = @sessionIndex
            RETURNING
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("gameStateId", gameStateId);
        cmd.Parameters.AddWithValue("sessionIndex", sessionIndex);
        cmd.Parameters.Add("sessionState", NpgsqlDbType.Jsonb).Value = SerializeState(sessionState);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    public async Task<GameSessionRecord?> MarkCompletedAsync(
        Guid playerId,
        Guid gameSessionId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE game_sessions
            SET completed_at = @completedAt,
                updated_at = NOW()
            WHERE player_id = @playerId
              AND game_session_id = @id
            RETURNING
                game_session_id,
                game_state_id,
                player_id,
                session_index,
                session_state,
                created_at,
                updated_at,
                completed_at;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("id", gameSessionId);
        cmd.Parameters.AddWithValue("completedAt", completedAt.UtcDateTime);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    private static async Task<int> GetNextSessionIndexAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid gameStateId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT session_index
            FROM game_sessions
            WHERE game_state_id = @gameStateId
            ORDER BY session_index DESC
            LIMIT 1
            FOR UPDATE;
            """;
        cmd.Parameters.AddWithValue("gameStateId", gameStateId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.CloseAsync();
            return 1;
        }

        var current = reader.GetInt32(0);
        await reader.CloseAsync();
        return checked(current + 1);
    }

    private static GameSessionRecord ReadSession(NpgsqlDataReader reader)
    {
        var sessionId = reader.GetGuid(0);
        var gameStateId = reader.GetGuid(1);
        var playerId = reader.GetGuid(2);
        var sessionIndex = reader.GetInt32(3);
        var stateJson = reader.GetFieldValue<string>(4);
        var createdAt = reader.GetFieldValue<DateTime>(5);
        var updatedAt = reader.GetFieldValue<DateTime>(6);
        var completedAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetFieldValue<DateTime>(7);

        var state = JsonSerializer.Deserialize<JsonElement>(stateJson);

        return new GameSessionRecord(
            sessionId,
            gameStateId,
            playerId,
            sessionIndex,
            state,
            createdAt,
            updatedAt,
            completedAt);
    }

    private static string SerializeState(JsonElement state)
    {
        return state.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : state.GetRawText();
    }
}
