
// Single Active Session manager (player -> single sess)
using Npgsql;

sealed class SessionManager
{
    private static readonly TimeSpan DefaultSlidingTtl = TimeSpan.FromHours(8);

    private readonly NpgsqlDataSource _dataSource;

    public SessionManager(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // Create a new session (replaces any previous one for the player)
    public async System.Threading.Tasks.Task<string> CreateOrReplaceAsync(string playerId, TimeSpan ttl)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            throw new ArgumentException("Invalid player id.", nameof(playerId));

        var sessionGuid = Guid.NewGuid();
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var revokeCmd = conn.CreateCommand())
        {
            revokeCmd.Transaction = tx;
            revokeCmd.CommandText =
                """
                UPDATE sessions
                SET revoked_at = NOW()
                WHERE player_id = @playerId
                  AND revoked_at IS NULL;
                """;
            revokeCmd.Parameters.AddWithValue("playerId", playerGuid);
            await revokeCmd.ExecuteNonQueryAsync();
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText =
                """
                INSERT INTO sessions (session_id, player_id, expires_at)
                VALUES (@sessionId, @playerId, @expiresAt);
                """;
            insertCmd.Parameters.AddWithValue("sessionId", sessionGuid);
            insertCmd.Parameters.AddWithValue("playerId", playerGuid);
            insertCmd.Parameters.AddWithValue("expiresAt", DateTime.UtcNow.Add(ttl));
            await insertCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return sessionGuid.ToString();
    }

    // Validate that sessId is the active one for this player
    public async System.Threading.Tasks.Task<bool> ValidateAsync(string playerId, string sessId, bool sliding)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            return false;
        if (!Guid.TryParse(sessId, out var sessionGuid) || sessionGuid == Guid.Empty)
            return false;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT expires_at
            FROM sessions
            WHERE player_id = @playerId
              AND session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("playerId", playerGuid);
        cmd.Parameters.AddWithValue("sessionId", sessionGuid);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return false;

        var expiresAt = reader.IsDBNull(0) ? (DateTime?)null : reader.GetFieldValue<DateTime>(0);
        await reader.CloseAsync();

        if (expiresAt is DateTime exp && exp < DateTime.UtcNow)
        {
            await MarkRevokedAsync(conn, sessionGuid);
            return false;
        }

        if (sliding)
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText =
                """
                UPDATE sessions
                SET expires_at = @expiresAt
                WHERE session_id = @sessionId;
                """;
            updateCmd.Parameters.AddWithValue("expiresAt", DateTime.UtcNow.Add(DefaultSlidingTtl));
            updateCmd.Parameters.AddWithValue("sessionId", sessionGuid);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return true;
    }

    // If this sessId is active for the player, revoke it
    public async System.Threading.Tasks.Task RevokeIfActiveAsync(string playerId, string sessId)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            return;
        if (!Guid.TryParse(sessId, out var sessionGuid) || sessionGuid == Guid.Empty)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE sessions
            SET revoked_at = NOW()
            WHERE player_id = @playerId
              AND session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("playerId", playerGuid);
        cmd.Parameters.AddWithValue("sessionId", sessionGuid);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async System.Threading.Tasks.Task MarkRevokedAsync(NpgsqlConnection conn, Guid sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE sessions
            SET revoked_at = NOW()
            WHERE session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }
}
