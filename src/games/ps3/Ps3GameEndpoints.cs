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
            var shortId = await ResolvePlayerShortIdAsync(devices, identity, playerId);

            if (string.IsNullOrWhiteSpace(shortId))
                return Results.BadRequest(new { error = "Missing player short id." });

            var definition = await games.TryGetAsync(GameSlug);
            if (definition is not GameDefinition game)
                return Results.Json(
                    new { error = $"Game definition '{GameSlug}' not found." },
                    statusCode: StatusCodes.Status500InternalServerError);

            var initialState = BuildInitialState(game, shortId);
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
            var shortId = await ResolvePlayerShortIdAsync(devices, identity, playerId);

            if (string.IsNullOrWhiteSpace(shortId))
                return Results.BadRequest(new { error = "Missing player short id." });

            JsonElement payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            if (!TryBuildStateWithPlayer(payload, shortId, out var nextState))
                return Results.BadRequest(new { error = "State payload must be a JSON object." });

            var updated = await games.UpdateStateAsync(playerId, gameStateId, nextState);
            if (updated is not { } result || !string.Equals(result.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                result.Slug,
                result.GameStateId,
                result.State.Clone()));
        });

        group.MapPost("/state/merge/{gameStateId:guid}", async (HttpRequest req, Guid gameStateId, GameStore games, DeviceStore devices) =>
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
            var shortId = await ResolvePlayerShortIdAsync(devices, identity, playerId);
            if (string.IsNullOrWhiteSpace(shortId))
                return Results.BadRequest(new { error = "Missing player short id." });

            JsonElement payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            JsonObject? patch;
            try
            {
                patch = payload.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(payload.GetRawText()) as JsonObject
                    : null;
            }
            catch
            {
                patch = null;
            }

            if (patch is null)
                return Results.BadRequest(new { error = "State payload must be a JSON object." });

            var existing = await games.TryGetStateAsync(playerId, gameStateId);
            if (existing is not { } current || !string.Equals(current.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            JsonObject baseState;
            try
            {
                baseState = current.State.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(current.State.GetRawText()) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch
            {
                baseState = new JsonObject();
            }

            foreach (var kvp in patch)
                baseState[kvp.Key] = kvp.Value?.DeepClone();

            baseState["playerId"] = shortId;

            var mergedState = JsonSerializer.SerializeToElement(baseState);
            var updated = await games.UpdateStateAsync(playerId, gameStateId, mergedState);
            if (updated is not { } result || !string.Equals(result.Slug, GameSlug, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Game state not found." });

            return Results.Json(new Ps3StateResponse(
                result.Slug,
                result.GameStateId,
                result.State.Clone()));
        });
    }
    private static JsonElement BuildInitialState(GameDefinition definition, string playerId)
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

    private static bool TryBuildStateWithPlayer(JsonElement source, string playerId, out JsonElement state)
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

        obj["playerId"] = playerId;
        state = JsonSerializer.SerializeToElement(obj);
        return true;
    }

    private static async Task<string?> ResolvePlayerShortIdAsync(DeviceStore devices, RequestIdentity identity, Guid playerId)
    {
        if (!string.IsNullOrWhiteSpace(identity.PlayerShortId))
            return identity.PlayerShortId;

        var context = await devices.TryGetAsync(playerId.ToString(), identity.DeviceId);
        return context?.PlayerShortId;
    }
}

sealed record Ps3StateResponse(string Game, Guid GameStateId, JsonElement State);
