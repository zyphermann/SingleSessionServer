using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal static class Ps3GameEndpoints
{
    private const string GameSlug = "ps3";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup($"/api/games/{GameSlug}")
                       .WithMetadata(EndpointAccessMetadata.Private);

        group.MapPost("/state/init", async (HttpRequest req, Ps3StateInitRequest? initRequest, GameStore games, DeviceStore devices) =>
        {
            Guid? playerId = null;

            if (req.Cookies.TryGetValue("player_id", out var playerCookie) && Guid.TryParse(playerCookie, out var cookiePlayerId))
                playerId = cookiePlayerId;

            if (playerId is null && initRequest is not null && !string.IsNullOrWhiteSpace(initRequest.PlayerId))
            {
                var raw = initRequest.PlayerId.Trim();
                if (Guid.TryParse(raw, out var parsed))
                    playerId = parsed;
            }

            if (playerId is null && initRequest is not null)
            {
                var shortId = initRequest.PlayerShortId;
                if (string.IsNullOrWhiteSpace(shortId))
                    shortId = initRequest.PlayerIdShort;

                if (!string.IsNullOrWhiteSpace(shortId))
                    playerId = await devices.TryGetPlayerIdByShortIdAsync(shortId.Trim());
            }

            if (playerId is null)
                return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or playerId/player_id_short in body." });

            var definition = await games.TryGetAsync(GameSlug);
            if (definition is not GameDefinition game)
                return Results.Json(
                    new { error = $"Game definition '{GameSlug}' not found." },
                    statusCode: StatusCodes.Status500InternalServerError);

            var resolvedPlayerId = playerId.Value;

            var initialState = BuildInitialState(game, resolvedPlayerId);
            var persisted = await games.UpsertStateAsync(resolvedPlayerId, game, initialState);

            return Results.Json(new Ps3StateResponse(
                game.Slug,
                persisted.GameStateId,
                persisted.State.Clone()));
        });

        group.MapPost("/state/read/{gameStateId:guid}", async (HttpRequest req, Ps3StateReadRequest? readRequest, Guid gameStateId, GameStore games, DeviceStore devices) =>
        {
            Guid? playerId = null;

            if (req.Cookies.TryGetValue("player_id", out var playerCookie) && Guid.TryParse(playerCookie, out var cookiePlayerId))
                playerId = cookiePlayerId;

            if (playerId is null && readRequest is not null && !string.IsNullOrWhiteSpace(readRequest.PlayerId))
            {
                var raw = readRequest.PlayerId.Trim();
                if (Guid.TryParse(raw, out var parsed))
                    playerId = parsed;
            }

            if (playerId is null && readRequest is not null)
            {
                var shortId = readRequest.PlayerShortId;
                if (string.IsNullOrWhiteSpace(shortId))
                    shortId = readRequest.PlayerIdShort;

                if (!string.IsNullOrWhiteSpace(shortId))
                    playerId = await devices.TryGetPlayerIdByShortIdAsync(shortId.Trim());
            }

            if (playerId is null)
                return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or playerId/player_id_short in body." });

            var snapshot = await games.TryGetStateAsync(playerId.Value, gameStateId);
            if (snapshot is not { } state || !string.Equals(state.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                state.Slug,
                state.GameStateId,
                state.State.Clone()));
        });

        group.MapPost("/state/write/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var playerCookie) || !Guid.TryParse(playerCookie, out var playerId))
                return Results.BadRequest(new { error = "Missing or invalid player_id cookie. Call /device/init first." });

            JsonElement payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            if (!TryBuildStateWithPlayer(payload, playerId, out var nextState))
                return Results.BadRequest(new { error = "State payload must be a JSON object." });

            var updated = await games.UpdateStateAsync(playerId, gameStateId, nextState);
            if (updated is not { } result || !string.Equals(result.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                result.Slug,
                result.GameStateId,
                result.State.Clone()));
        });
    }

    private static JsonElement BuildInitialState(GameDefinition definition, Guid playerId)
    {
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(definition.DefaultStateJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            obj = new JsonObject();
        }

        obj["playerId"] = playerId.ToString();
        return JsonSerializer.SerializeToElement(obj);
    }

    private static bool TryBuildStateWithPlayer(JsonElement source, Guid playerId, out JsonElement state)
    {
        JsonObject? obj;
        try
        {
            obj = source.ValueKind switch
            {
                JsonValueKind.Object => JsonNode.Parse(source.GetRawText()) as JsonObject ?? new JsonObject(),
                JsonValueKind.Null or JsonValueKind.Undefined => new JsonObject(),
                _ => null
            };
        }
        catch
        {
            obj = null;
        }

        if (obj is null)
        {
            state = default;
            return false;
        }

        obj["playerId"] = playerId.ToString();
        state = JsonSerializer.SerializeToElement(obj);
        return true;
    }
}

sealed record Ps3StateResponse(string Game, Guid GameStateId, JsonElement State);

sealed record Ps3StateInitRequest(
    string? PlayerId,
    string? PlayerShortId,
    [property: JsonPropertyName("player_id_short")] string? PlayerIdShort);

sealed record Ps3StateReadRequest(
    string? PlayerId,
    string? PlayerShortId,
    [property: JsonPropertyName("player_id_short")] string? PlayerIdShort);
