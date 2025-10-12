using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Ps3GameEndpoints
{
    private const string GameSlug = "ps3";

    public static void MapPs3GameEndpoints(this WebApplication app)
    {
        var group = app.MapGroup($"/api/games/{GameSlug}")
                       .WithMetadata(EndpointAccessMetadata.Private);

        group.MapPost("/state/init", async (HttpRequest req, GameStore games) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var playerCookie) || !Guid.TryParse(playerCookie, out var playerId))
                return Results.BadRequest(new { error = "Missing or invalid player_id cookie. Call /device/init first." });

            var definition = await games.TryGetAsync(GameSlug);
            if (definition is not GameDefinition game)
                return Results.Json(
                    new { error = $"Game definition '{GameSlug}' not found." },
                    statusCode: StatusCodes.Status500InternalServerError);

            var initialState = BuildInitialState(game, playerId);
            var persisted = await games.UpsertStateAsync(playerId, game, initialState);

            return Results.Json(new Ps3StateResponse(
                game.Slug,
                persisted.GameStateId,
                persisted.State.Clone()));
        });

        group.MapGet("/state/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var playerCookie) || !Guid.TryParse(playerCookie, out var playerId))
                return Results.BadRequest(new { error = "Missing or invalid player_id cookie. Call /device/init first." });

            var snapshot = await games.TryGetStateAsync(playerId, gameStateId);
            if (snapshot is not { } state || !string.Equals(state.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                state.Slug,
                state.GameStateId,
                state.State.Clone()));
        });

        group.MapPost("/state/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games) =>
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
