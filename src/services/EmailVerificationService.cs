using Npgsql;
using System;
using System.Threading.Tasks;

sealed class EmailVerificationService
{
    private readonly NpgsqlDataSource _dataSource;

    public EmailVerificationService(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<EmailVerificationPending> CreateAsync(Guid playerId, string email, TimeSpan ttl)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Clean up existing pending verifications for this player
        await using (var cleanup = conn.CreateCommand())
        {
            cleanup.Transaction = tx;
            cleanup.CommandText = "DELETE FROM email_verifications WHERE player_id = @playerId;";
            cleanup.Parameters.AddWithValue("playerId", playerId);
            await cleanup.ExecuteNonQueryAsync();
        }

        var token = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO email_verifications (player_id, email, token, expires_at)
                VALUES (@playerId, @email, @token, @expiresAt);
                """;
            insert.Parameters.AddWithValue("playerId", playerId);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("token", token);
            insert.Parameters.AddWithValue("expiresAt", expiresAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return new EmailVerificationPending(token, email, expiresAt);
    }

    public async Task<EmailVerificationResult> ConfirmAsync(Guid token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        Guid playerId;
        string email;
        DateTimeOffset expiresAt;

        await using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText =
                """
                SELECT player_id, email, expires_at
                FROM email_verifications
                WHERE token = @token
                FOR UPDATE;
                """;
            select.Parameters.AddWithValue("token", token);

            await using var reader = await select.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return EmailVerificationResult.NotFound;

            playerId = reader.GetGuid(0);
            email = reader.GetFieldValue<string>(1);
            var expiresAtRaw = reader.GetFieldValue<DateTime>(2);
            expiresAt = DateTime.SpecifyKind(expiresAtRaw, DateTimeKind.Utc);
        }

        if (expiresAt < DateTimeOffset.UtcNow)
        {
            await DeleteByTokenAsync(conn, tx, token);
            await tx.CommitAsync();
            return EmailVerificationResult.Expired;
        }

        try
        {
            await using var updatePlayer = conn.CreateCommand();
            updatePlayer.Transaction = tx;
            updatePlayer.CommandText =
                """
                UPDATE players
                SET email = @email,
                    email_verified_at = NOW(),
                    updated_at = NOW()
                WHERE player_id = @playerId;
                """;
            updatePlayer.Parameters.AddWithValue("email", email);
            updatePlayer.Parameters.AddWithValue("playerId", playerId);
            await updatePlayer.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync();
            return EmailVerificationResult.EmailAlreadyTaken;
        }

        await DeleteByTokenAsync(conn, tx, token);
        await tx.CommitAsync();
        return EmailVerificationResult.Success;
    }

    private static async Task DeleteByTokenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid token)
    {
        await using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM email_verifications WHERE token = @token;";
        delete.Parameters.AddWithValue("token", token);
        await delete.ExecuteNonQueryAsync();
    }
}

readonly record struct EmailVerificationPending(Guid Token, string Email, DateTimeOffset ExpiresAt);

enum EmailVerificationResult
{
    Success,
    NotFound,
    Expired,
    EmailAlreadyTaken
}
