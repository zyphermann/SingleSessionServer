using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Ps3GameEndpoints
{
    private const string GameSlug = "ps3";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup($"/api/games/{GameSlug}")
                       .WithMetadata(EndpointAccessMetadata.Private);

        group.MapPost("/state/init", async (HttpRequest req, GameStore games, DeviceStore devices) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
            }
            catch (RequestIdentityException)
            {
                return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
            }

            var playerId = identity.PlayerId!.Value;

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

        group.MapGet("/state/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games, DeviceStore devices) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
            }
            catch (RequestIdentityException)
            {
                return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
            }

            var playerId = identity.PlayerId!.Value;

            var snapshot = await games.TryGetStateAsync(playerId, gameStateId);
            if (snapshot is not { } state || !string.Equals(state.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                state.Slug,
                state.GameStateId,
                state.State.Clone()));
        });

        group.MapPost("/state/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games, DeviceStore devices) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
            }
            catch (RequestIdentityException)
            {
                return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
            }

            var playerId = identity.PlayerId!.Value;

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
