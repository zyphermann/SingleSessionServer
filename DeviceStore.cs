using Npgsql;
using System;
using System.Threading.Tasks;

sealed class DeviceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public DeviceStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string> EnsureAsync(string? playerIdRaw)
    {
        var hasValidId = Guid.TryParse(playerIdRaw, out var playerId);
        if (!hasValidId || playerId == Guid.Empty)
            playerId = Guid.NewGuid();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO devices (player_id, last_seen_at)
            VALUES (@playerId, NOW())
            ON CONFLICT (player_id) DO UPDATE
                SET last_seen_at = EXCLUDED.last_seen_at
            RETURNING player_id;
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        var result = (Guid)(await cmd.ExecuteScalarAsync())!;
        return result.ToString();
    }

    public async Task<bool> TrySetEmailAsync(string playerIdRaw, string email)
    {
        if (!Guid.TryParse(playerIdRaw, out var playerId) || playerId == Guid.Empty)
            return false;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO devices (player_id, email, last_seen_at)
            VALUES (@playerId, @Email, NOW())
            ON CONFLICT (player_id) DO UPDATE
                SET email = EXCLUDED.email,
                    last_seen_at = NOW();
            """;
        cmd.Parameters.AddWithValue("playerId", playerId);
        cmd.Parameters.AddWithValue("Email", email);

        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task TouchAsync(string playerIdRaw)
    {
        if (!Guid.TryParse(playerIdRaw, out var playerId) || playerId == Guid.Empty)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_seen_at = NOW() WHERE player_id = @playerId;";
        cmd.Parameters.AddWithValue("playerId", playerId);
        await cmd.ExecuteNonQueryAsync();
    }
}
