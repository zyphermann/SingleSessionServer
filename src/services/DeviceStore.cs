using Npgsql;

sealed record DeviceContext(Guid PlayerId, Guid DeviceId, string PlayerShortId)
{
    public string PlayerIdString => PlayerId.ToString();
    public string DeviceIdString => DeviceId.ToString();
}

sealed class DeviceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public DeviceStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<DeviceContext> EnsureAsync(string? playerIdRaw, string? deviceIdRaw)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();

        if (Guid.TryParse(deviceIdRaw, out var deviceId))
        {
            var existing = await TryLoadByDeviceAsync(conn, deviceId);
            if (existing is not null)
            {
                await TouchInternalAsync(conn, existing);
                return existing;
            }
        }

        if (Guid.TryParse(playerIdRaw, out var playerId))
        {
            var ctx = await TryCreateDeviceForPlayerAsync(conn, playerId);
            if (ctx is not null)
                return ctx;
        }

        return await CreateFreshAsync(conn);
    }

    public async Task<DeviceContext?> TryGetAsync(string? playerIdRaw, string? deviceIdRaw)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();

        if (Guid.TryParse(deviceIdRaw, out var deviceId))
        {
            var existing = await TryLoadByDeviceAsync(conn, deviceId);
            if (existing is not null)
            {
                await TouchInternalAsync(conn, existing);
                return existing;
            }
        }

        if (Guid.TryParse(playerIdRaw, out var playerId))
        {
            var existing = await TryLoadByPlayerAsync(conn, playerId);
            if (existing is not null)
            {
                await TouchInternalAsync(conn, existing);
                return existing;
            }
        }

        return null;
    }

    public async Task<Guid?> TryGetPlayerIdByShortIdAsync(string? shortId)
    {
        if (string.IsNullOrWhiteSpace(shortId))
            return null;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT player_id FROM players WHERE short_id = @shortId;";
        cmd.Parameters.AddWithValue("shortId", shortId);
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid playerId ? playerId : null;
    }

    public async Task<DeviceContext> AttachEmailAsync(DeviceContext context, string email)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var currentPlayerId = context.PlayerId;
        var currentDeviceId = context.DeviceId;

        // Lock current player row
        if (!await EnsurePlayerExistsAsync(conn, tx, currentPlayerId))
            throw new InvalidOperationException("Player not found for current device.");

        // Find existing owner of email (if any)
        Guid? existingOwner = null;
        await using (var findOwner = conn.CreateCommand())
        {
            findOwner.Transaction = tx;
            findOwner.CommandText = "SELECT player_id FROM players WHERE email = @email FOR UPDATE;";
            findOwner.Parameters.AddWithValue("email", email);
            var result = await findOwner.ExecuteScalarAsync();
            if (result is Guid guid)
                existingOwner = guid;
        }

        DeviceContext finalContext;

        if (existingOwner is null)
        {
            await using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE players
                SET email = @email,
                    updated_at = NOW()
                WHERE player_id = @playerId;
                """;
            update.Parameters.AddWithValue("email", email);
            update.Parameters.AddWithValue("playerId", currentPlayerId);
            await update.ExecuteNonQueryAsync();

            finalContext = context;
        }
        else if (existingOwner == currentPlayerId)
        {
            finalContext = context;
        }
        else
        {
            // Move all devices to existing owner
            await using (var moveDevices = conn.CreateCommand())
            {
                moveDevices.Transaction = tx;
                moveDevices.CommandText =
                    """
                    UPDATE devices
                    SET player_id = @target,
                        last_seen_at = NOW()
                    WHERE player_id = @source;
                    """;
                moveDevices.Parameters.AddWithValue("target", existingOwner.Value);
                moveDevices.Parameters.AddWithValue("source", currentPlayerId);
                await moveDevices.ExecuteNonQueryAsync();
            }

            // Remove sessions tied to the now-orphaned player
            await using (var deleteSessions = conn.CreateCommand())
            {
                deleteSessions.Transaction = tx;
                deleteSessions.CommandText = "DELETE FROM sessions WHERE player_id = @playerId;";
                deleteSessions.Parameters.AddWithValue("playerId", currentPlayerId);
                await deleteSessions.ExecuteNonQueryAsync();
            }

            // Delete the old player row if it is now unused
            await using (var deletePlayer = conn.CreateCommand())
            {
                deletePlayer.Transaction = tx;
                deletePlayer.CommandText = "DELETE FROM players WHERE player_id = @playerId;";
                deletePlayer.Parameters.AddWithValue("playerId", currentPlayerId);
                await deletePlayer.ExecuteNonQueryAsync();
            }

            await using (var bumpOwner = conn.CreateCommand())
            {
                bumpOwner.Transaction = tx;
                bumpOwner.CommandText = "UPDATE players SET updated_at = NOW() WHERE player_id = @target;";
                bumpOwner.Parameters.AddWithValue("target", existingOwner.Value);
                await bumpOwner.ExecuteNonQueryAsync();
            }

            var targetShortId = await GetPlayerShortIdAsync(conn, existingOwner.Value, tx);
            finalContext = context with { PlayerId = existingOwner.Value, PlayerShortId = targetShortId };
        }

        await tx.CommitAsync();
        await TouchInternalAsync(conn, finalContext);
        return finalContext;
    }

    public async Task<DeviceContext> BindDeviceAsync(DeviceContext context, string targetPlayerIdRaw)
    {
        if (!Guid.TryParse(targetPlayerIdRaw, out var targetPlayerId) || targetPlayerId == Guid.Empty)
            throw new ArgumentException("Invalid target player id.", nameof(targetPlayerIdRaw));

        if (context.PlayerId == targetPlayerId)
            return context;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Ensure target player exists
        if (!await EnsurePlayerExistsAsync(conn, tx, targetPlayerId))
            throw new InvalidOperationException("Target player not found.");

        // Move all devices from current player to target
        await using (var moveDevices = conn.CreateCommand())
        {
            moveDevices.Transaction = tx;
            moveDevices.CommandText =
                """
                UPDATE devices
                SET player_id = @target,
                    last_seen_at = NOW()
                WHERE player_id = @source;
                """;
            moveDevices.Parameters.AddWithValue("target", targetPlayerId);
            moveDevices.Parameters.AddWithValue("source", context.PlayerId);
            await moveDevices.ExecuteNonQueryAsync();
        }

        // Delete sessions tied to previous player id
        await using (var deleteSessions = conn.CreateCommand())
        {
            deleteSessions.Transaction = tx;
            deleteSessions.CommandText = "DELETE FROM sessions WHERE player_id = @playerId;";
            deleteSessions.Parameters.AddWithValue("playerId", context.PlayerId);
            await deleteSessions.ExecuteNonQueryAsync();
        }

        // Delete old player row
        await using (var deletePlayer = conn.CreateCommand())
        {
            deletePlayer.Transaction = tx;
            deletePlayer.CommandText = "DELETE FROM players WHERE player_id = @playerId;";
            deletePlayer.Parameters.AddWithValue("playerId", context.PlayerId);
            await deletePlayer.ExecuteNonQueryAsync();
        }

        await using (var bumpOwner = conn.CreateCommand())
        {
            bumpOwner.Transaction = tx;
            bumpOwner.CommandText = "UPDATE players SET updated_at = NOW() WHERE player_id = @target;";
            bumpOwner.Parameters.AddWithValue("target", targetPlayerId);
            await bumpOwner.ExecuteNonQueryAsync();
        }

        var targetShortId = await GetPlayerShortIdAsync(conn, targetPlayerId, tx);
        await tx.CommitAsync();

        var updated = context with { PlayerId = targetPlayerId, PlayerShortId = targetShortId };
        await TouchInternalAsync(conn, updated);
        return updated;
    }

    public async Task TouchAsync(string? playerIdRaw, string? deviceIdRaw)
    {
        var ctx = await TryGetAsync(playerIdRaw, deviceIdRaw);
        if (ctx is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await TouchInternalAsync(conn, ctx);
        }
    }

    public async Task TouchAsync(DeviceContext context)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await TouchInternalAsync(conn, context);
    }

    private static async Task<DeviceContext?> TryLoadByDeviceAsync(NpgsqlConnection conn, Guid deviceId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.player_id, p.short_id
            FROM devices d
            JOIN players p ON p.player_id = d.player_id
            WHERE d.device_id = @deviceId;
            """;
        cmd.Parameters.AddWithValue("deviceId", deviceId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var playerId = reader.GetGuid(0);
        var shortId = reader.GetString(1);
        return new DeviceContext(playerId, deviceId, shortId);
    }

    private static async Task<DeviceContext?> TryLoadByPlayerAsync(NpgsqlConnection conn, Guid playerId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT d.device_id, p.short_id
            FROM devices d
            JOIN players p ON p.player_id = d.player_id
            WHERE d.player_id = @playerId
            ORDER BY d.last_seen_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var deviceId = reader.GetGuid(0);
        var shortId = reader.GetString(1);
        return new DeviceContext(playerId, deviceId, shortId);
    }

    private static async Task<DeviceContext?> TryCreateDeviceForPlayerAsync(NpgsqlConnection conn, Guid playerId)
    {
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM players WHERE player_id = @playerId;";
            check.Parameters.AddWithValue("playerId", playerId);
            var exists = await check.ExecuteScalarAsync();
            if (exists is null)
                return null;
        }

        var deviceId = Guid.NewGuid();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO devices (device_id, player_id)
                VALUES (@deviceId, @playerId);
                """;
            insert.Parameters.AddWithValue("deviceId", deviceId);
            insert.Parameters.AddWithValue("playerId", playerId);
            await insert.ExecuteNonQueryAsync();
        }

        var shortId = await GetPlayerShortIdAsync(conn, playerId);
        var ctx = new DeviceContext(playerId, deviceId, shortId);
        await TouchInternalAsync(conn, ctx);
        return ctx;
    }

    private static async Task<DeviceContext> CreateFreshAsync(NpgsqlConnection conn)
    {
        await using var tx = await conn.BeginTransactionAsync();
        var playerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var shortId = await GenerateUniqueShortIdAsync(conn, tx);

        await using (var insertPlayer = conn.CreateCommand())
        {
            insertPlayer.Transaction = tx;
            insertPlayer.CommandText =
                """
                INSERT INTO players (player_id, email, short_id, created_at, updated_at)
                VALUES (@playerId, NULL, @shortId, NOW(), NOW());
                """;
            insertPlayer.Parameters.AddWithValue("playerId", playerId);
            insertPlayer.Parameters.AddWithValue("shortId", shortId);
            await insertPlayer.ExecuteNonQueryAsync();
        }

        await using (var insertDevice = conn.CreateCommand())
        {
            insertDevice.Transaction = tx;
            insertDevice.CommandText =
                """
                INSERT INTO devices (device_id, player_id, created_at, last_seen_at)
                VALUES (@deviceId, @playerId, NOW(), NOW());
                """;
            insertDevice.Parameters.AddWithValue("deviceId", deviceId);
            insertDevice.Parameters.AddWithValue("playerId", playerId);
            await insertDevice.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        return new DeviceContext(playerId, deviceId, shortId);
    }

    private static async Task TouchInternalAsync(NpgsqlConnection conn, DeviceContext context)
    {
        await using (var updateDevice = conn.CreateCommand())
        {
            updateDevice.CommandText =
                """
                UPDATE devices
                SET last_seen_at = NOW()
                WHERE device_id = @deviceId;
                """;
            updateDevice.Parameters.AddWithValue("deviceId", context.DeviceId);
            await updateDevice.ExecuteNonQueryAsync();
        }

        await using (var updatePlayer = conn.CreateCommand())
        {
            updatePlayer.CommandText =
                """
                UPDATE players
                SET updated_at = NOW()
                WHERE player_id = @playerId;
                """;
            updatePlayer.Parameters.AddWithValue("playerId", context.PlayerId);
            await updatePlayer.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> GenerateUniqueShortIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int length = 8)
    {
        while (true)
        {
            var candidate = NanoId.Generate(length);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT 1 FROM players WHERE short_id = @shortId;";
            cmd.Parameters.AddWithValue("shortId", candidate);
            var exists = await cmd.ExecuteScalarAsync();
            if (exists is null)
                return candidate;
        }
    }

    private static async Task<string> GetPlayerShortIdAsync(NpgsqlConnection conn, Guid playerId, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT short_id FROM players WHERE player_id = @playerId;";
        cmd.Parameters.AddWithValue("playerId", playerId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is string shortId)
            return shortId;
        throw new InvalidOperationException("Player short id not found.");
    }

    private static async Task<bool> EnsurePlayerExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid playerId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM players WHERE player_id = @playerId FOR UPDATE;";
        cmd.Parameters.AddWithValue("playerId", playerId);
        var exists = await cmd.ExecuteScalarAsync();
        return exists is not null;
    }
}
