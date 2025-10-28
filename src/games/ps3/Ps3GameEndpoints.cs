using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

sealed record Ps3GameSessionStartRequest
{
    public JsonElement? State { get; init; }
}

sealed record Ps3GameSessionPlayRequest
{
    public JsonElement? State { get; init; }
    public bool? Complete { get; init; }
}

sealed record Ps3GameSessionResponse(
    Guid GameStateId,
    Guid GameSessionId,
    int SessionIndex,
    JsonElement SessionState,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt);

internal static class Ps3GameSessionEndpoints
{
    private const string GameSlug = "ps3";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup($"/api/games/{GameSlug}")
                       .WithMetadata(EndpointAccessMetadata.Private);

        group.MapGet(
            "/game/{gameStateId:guid}/continue",
            async (HttpRequest req,
                Guid gameStateId,
                GameStore games,
                GameSessionStore sessions,
                DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }
                var playerId = identity.PlayerId!.Value;
                var gameState = await games.TryGetStateAsync(playerId, gameStateId);

                if (gameState is not { } stateSnapshot || !string.Equals(stateSnapshot.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { error = "Game state not found." });

                var session = await sessions.TryGetLatestAsync(playerId, gameStateId);
                if (session is null)
                {
                    return Results.Json(new { ok = false, message = "No game session found." });
                }

                return Results.Json(ToResponse(session));
            }
        );
        group.MapPost(
            "/game/{gameStateId:guid}/start",
            async (HttpRequest req,
                Guid gameStateId,
                GameStore games,
                GameSessionStore sessions,
                DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }

                var playerId = identity.PlayerId!.Value;
                var gameState = await games.TryGetStateAsync(playerId, gameStateId);
                if (gameState is not { } stateSnapshot || !string.Equals(stateSnapshot.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { error = "Game state not found." });

                Ps3GameSessionStartRequest request;
                try
                {
                    request = await DeserializeOrDefaultAsync<Ps3GameSessionStartRequest>(req);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid JSON payload." });
                }

                if (request.State is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined })
                    return Results.BadRequest(new { error = "Session state must be a JSON object." });

                var initialState = request.State.HasValue
                    ? request.State.Value.Clone()
                    : CreateEmptyObjectState();

                GameSessionRecord session = await sessions.CreateAsync(playerId, gameStateId, initialState);
                return Results.Json(ToResponse(session));
            });

        group.MapPost(
            "/game/{gameStateId:guid}/play/{index:int}",
            async (HttpRequest req, Guid gameStateId, int index, GameStore games, GameSessionStore sessions, DeviceStore devices) =>
            {
                if (index < 1)
                    return Results.BadRequest(new { error = "Session index must be greater than zero." });

                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }

                var playerId = identity.PlayerId!.Value;
                var gameState = await games.TryGetStateAsync(playerId, gameStateId);
                if (gameState is not { } stateSnapshot || !string.Equals(stateSnapshot.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { error = "Game state not found." });

                Ps3GameSessionPlayRequest request;
                try
                {
                    request = await DeserializeOrDefaultAsync<Ps3GameSessionPlayRequest>(req);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid JSON payload." });
                }

                if (request.State is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined })
                    return Results.BadRequest(new { error = "Session state must be a JSON object." });

                var sessionState = request.State.HasValue
                    ? request.State.Value.Clone()
                    : CreateEmptyObjectState();

                var updated = await sessions.UpdateStateAsync(playerId, gameStateId, index, sessionState);
                if (updated is null)
                    return Results.NotFound(new { error = "Game session not found." });

                var response = updated;

                if (request.Complete is true)
                {
                    response = await sessions.MarkCompletedAsync(playerId, updated.GameSessionId, DateTimeOffset.UtcNow) ?? updated;
                }

                return Results.Json(ToResponse(response));
            });
    }

    private static async Task<T> DeserializeOrDefaultAsync<T>(HttpRequest req) where T : new()
    {
        if (req.ContentLength is 0)
            return new T();

        try
        {
            var payload = await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions);
            return payload ?? new T();
        }
        catch (JsonException)
        {
            throw;
        }
    }

    private static Ps3GameSessionResponse ToResponse(GameSessionRecord session)
    {
        return new Ps3GameSessionResponse(
            session.GameStateId,
            session.GameSessionId,
            session.SessionIndex,
            session.SessionState.Clone(),
            session.CreatedAt,
            session.UpdatedAt,
            session.CompletedAt);
    }

    private static JsonElement CreateEmptyObjectState()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
