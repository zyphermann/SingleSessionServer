using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

readonly record struct GameDefinition(Guid GameId, string Slug, string DisplayName, string DefaultStateJson);

sealed record GameLoadResult(GameDefinition Definition, JsonElement State, bool Created, Guid? GameStateId = null);

sealed record GameStateUpsertResult(Guid GameStateId, string Slug, JsonElement State);

sealed class GameStore
{
    private readonly NpgsqlDataSource _dataSource;

    public GameStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<GameDefinition>> ListAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT game_id, slug, display_name, default_state
            FROM games
            ORDER BY display_name, slug;
            """;

        var result = new List<GameDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var gameId = reader.GetGuid(0);
            var slug = reader.GetFieldValue<string>(1);
            var displayName = reader.GetFieldValue<string>(2);
            var stateJson = reader.GetFieldValue<string>(3);
            result.Add(new GameDefinition(gameId, slug, displayName, stateJson));
        }

        return result;
    }

    public async Task<GameDefinition?> TryGetAsync(string slug)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        return await TryGetInternalAsync(conn, null, NormalizeSlug(slug));
    }

    public async Task<GameDefinition> UpsertAsync(string slug, string displayName, JsonElement defaultState)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name must not be empty.", nameof(displayName));

        var stateJson = defaultState.GetRawText();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO games (slug, display_name, default_state)
            VALUES (@slug, @displayName, @defaultState)
            ON CONFLICT (slug) DO UPDATE
            SET display_name = EXCLUDED.display_name,
                default_state = EXCLUDED.default_state,
                updated_at = NOW()
            RETURNING game_id, slug, display_name, default_state;
            """;
        cmd.Parameters.AddWithValue("slug", normalizedSlug);
        cmd.Parameters.AddWithValue("displayName", displayName.Trim());
        cmd.Parameters.Add("defaultState", NpgsqlDbType.Jsonb).Value = stateJson;

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Upsert failed for game definition.");

        var storedId = reader.GetGuid(0);
        var storedSlug = reader.GetFieldValue<string>(1);
        var storedName = reader.GetFieldValue<string>(2);
        var storedStateJson = reader.GetFieldValue<string>(3);

        return new GameDefinition(storedId, storedSlug, storedName, storedStateJson);
    }

    public async Task<GameLoadResult?> LoadAsync(Guid playerId, string slug)
    {
        var normalizedSlug = NormalizeSlug(slug);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var game = await TryGetInternalAsync(conn, tx, normalizedSlug);
        if (game is not GameDefinition definition)
            return null;

        var state = await LoadOrCreateStateAsync(conn, tx, playerId, definition);
        await tx.CommitAsync();
        return state;
    }

    private static async Task<GameDefinition?> TryGetInternalAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string normalizedSlug)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT game_id, slug, display_name, default_state
            FROM games
            WHERE slug = @slug;
            """;
        cmd.Parameters.AddWithValue("slug", normalizedSlug);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var gameId = reader.GetGuid(0);
        var slug = reader.GetFieldValue<string>(1);
        var displayName = reader.GetFieldValue<string>(2);
        var stateJson = reader.GetFieldValue<string>(3);
        await reader.CloseAsync();

        return new GameDefinition(gameId, slug, displayName, stateJson);
    }

    private static string NormalizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug must not be empty.", nameof(slug));
        return slug.Trim().ToLowerInvariant();
    }

    private static async Task<GameLoadResult> LoadOrCreateStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid playerId,
        GameDefinition game)
    {
        await using var selectState = conn.CreateCommand();
        selectState.Transaction = tx;
        selectState.CommandText =
            """
            SELECT game_state_id, state
            FROM game_states
            WHERE player_id = @playerId
              AND game_id = @gameId
            FOR UPDATE;
            """;
        selectState.Parameters.AddWithValue("playerId", playerId);
        selectState.Parameters.AddWithValue("gameId", game.GameId);

        await using var reader = await selectState.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var stateId = reader.GetGuid(0);
            var stateJson = reader.GetFieldValue<string>(1);
            var state = JsonSerializer.Deserialize<JsonElement>(stateJson);
            await reader.CloseAsync();

            await using var touch = conn.CreateCommand();
            touch.Transaction = tx;
            touch.CommandText =
                """
                UPDATE game_states
                SET updated_at = NOW()
                WHERE game_state_id = @id;
                """;
            touch.Parameters.AddWithValue("id", stateId);
            await touch.ExecuteNonQueryAsync();

            return new GameLoadResult(game, state, Created: false, GameStateId: stateId);
        }

        await reader.CloseAsync();

        var defaultStateJson = game.DefaultStateJson;
        await using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO game_states (game_state_id, game_id, player_id, state)
            VALUES (@id, @gameId, @playerId, @state)
            RETURNING game_state_id, state;
            """;
        var newStateId = Guid.NewGuid();
        insert.Parameters.AddWithValue("id", newStateId);
        insert.Parameters.AddWithValue("gameId", game.GameId);
        insert.Parameters.AddWithValue("playerId", playerId);
        insert.Parameters.Add("state", NpgsqlDbType.Jsonb).Value = defaultStateJson;

        await using var insertReader = await insert.ExecuteReaderAsync();
        if (!await insertReader.ReadAsync())
            throw new InvalidOperationException("Failed to create default game state.");

        var createdStateJson = insertReader.GetFieldValue<string>(1);
        var createdState = JsonSerializer.Deserialize<JsonElement>(createdStateJson);
        await insertReader.CloseAsync();

        return new GameLoadResult(game, createdState, Created: true, GameStateId: newStateId);
    }

    public async Task<GameStateUpsertResult> UpsertStateAsync(Guid playerId, GameDefinition game, JsonElement state)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        return await UpsertStateInternalAsync(conn, null, playerId, game, state);
    }

    public async Task<GameStateUpsertResult?> TryGetStateAsync(Guid playerId, Guid gameStateId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT gs.game_state_id, g.slug, gs.state
            FROM game_states gs
            JOIN games g ON g.game_id = gs.game_id
            WHERE gs.game_state_id = @id
              AND gs.player_id = @playerId;
            """;
        cmd.Parameters.AddWithValue("id", gameStateId);
        cmd.Parameters.AddWithValue("playerId", playerId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var id = reader.GetGuid(0);
        var slug = reader.GetFieldValue<string>(1);
        var stateJson = reader.GetFieldValue<string>(2);
        await reader.CloseAsync();

        return new GameStateUpsertResult(id, slug, JsonSerializer.Deserialize<JsonElement>(stateJson));
    }

    public async Task<GameStateUpsertResult?> UpdateStateAsync(Guid playerId, Guid gameStateId, JsonElement state)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE game_states gs
            SET state = @state,
                updated_at = NOW()
            FROM games g
            WHERE gs.game_state_id = @id
              AND gs.player_id = @playerId
              AND g.game_id = gs.game_id
            RETURNING gs.game_state_id, g.slug, gs.state;
            """;
        cmd.Parameters.AddWithValue("id", gameStateId);
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.Add("state", NpgsqlDbType.Jsonb).Value = state.GetRawText();

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var id = reader.GetGuid(0);
        var slug = reader.GetFieldValue<string>(1);
        var stateJson = reader.GetFieldValue<string>(2);
        await reader.CloseAsync();

        return new GameStateUpsertResult(id, slug, JsonSerializer.Deserialize<JsonElement>(stateJson));
    }

    private static async Task<GameStateUpsertResult> UpsertStateInternalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid playerId,
        GameDefinition game,
        JsonElement state)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO game_states (game_state_id, game_id, player_id, state)
            VALUES (@id, @gameId, @playerId, @state)
            ON CONFLICT (game_id, player_id) DO UPDATE
            SET state = EXCLUDED.state,
                updated_at = NOW()
            RETURNING game_state_id, state;
            """;
        var newId = Guid.NewGuid();
        cmd.Parameters.AddWithValue("id", newId);
        cmd.Parameters.AddWithValue("gameId", game.GameId);
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.Add("state", NpgsqlDbType.Jsonb).Value = state.GetRawText();

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Failed to upsert game state.");

        var gameStateId = reader.GetGuid(0);
        var stateJson = reader.GetFieldValue<string>(1);
        await reader.CloseAsync();

        return new GameStateUpsertResult(gameStateId, game.Slug, JsonSerializer.Deserialize<JsonElement>(stateJson));
    }
}

