using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

sealed record GameDefinitionRequest(string? DisplayName, JsonElement? DefaultState);

sealed record GameDefinitionResponse(Guid Id, string Slug, string DisplayName, JsonElement DefaultState);

sealed record LoadGameStateResponse(GameDefinitionResponse Definition, JsonElement State, bool Created);

sealed record GameStateResponse(string Game, Guid GameStateId, JsonElement State);

sealed record GameStateLoadRequest(
    string? PlayerId,
    string? PlayerShortId,
    [property: JsonPropertyName("player_id_short")] string? PlayerIdShort);

internal static class GameEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/games")
                       .WithMetadata(EndpointAccessMetadata.Private);

        group.MapGet("", async (GameStore store) =>
        {
            var defs = await store.ListAsync();
            var payload = defs.Select(def =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(def.DefaultStateJson);
                return new GameDefinitionResponse(def.GameId, def.Slug, def.DisplayName, json.Clone());
            });
            return Results.Json(payload);
        });

        group.MapGet(
            "/{slug}",
            async (string slug, GameStore games) =>
            {
                var def = await games.TryGetAsync(slug);
                if (def is not GameDefinition definition)
                    return Results.NotFound(new { error = "Game not found" });

                var json = JsonSerializer.Deserialize<JsonElement>(definition.DefaultStateJson);
                return Results.Json(new GameDefinitionResponse(definition.GameId, definition.Slug, definition.DisplayName, json.Clone()));
            });

        group.MapPut(
            "/{slug}",
            async (string slug, GameDefinitionRequest request, GameStore store) =>
            {
                if (request is null)
                    return Results.BadRequest(new { error = "Missing request body." });

                if (string.IsNullOrWhiteSpace(request.DisplayName))
                    return Results.BadRequest(new { error = "DisplayName is required." });

                JsonElement defaultStateElement;
                if (request.DefaultState.HasValue)
                {
                    defaultStateElement = request.DefaultState.Value.Clone();
                }
                else
                {
                    using var doc = JsonDocument.Parse("{}");
                    defaultStateElement = doc.RootElement.Clone();
                }

                var definition = await store.UpsertAsync(slug, request.DisplayName.Trim(), defaultStateElement);
                var json = JsonSerializer.Deserialize<JsonElement>(definition.DefaultStateJson);
                return Results.Json(new GameDefinitionResponse(definition.GameId, definition.Slug, definition.DisplayName, json.Clone()));
            });

        group.MapGet("/{slug}/state/{gameStateId:guid}", async (HttpRequest req, string slug, Guid gameStateId, GameStore games, DeviceStore devices) =>
               {
                   RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                   if (identity is null)
                   {
                       return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                   }

                   var playerId = identity.PlayerId!.Value;

                   var snapshot = await games.TryGetStateAsync(playerId, gameStateId);
                   if (snapshot is not { } state || !string.Equals(state.Slug, slug, StringComparison.OrdinalIgnoreCase))
                       return Results.NotFound(new { error = "Game state not found." });

                   return Results.Json(new GameStateResponse(
                       state.Slug,
                       state.GameStateId,
                       state.State.Clone()));
               });

        group.MapPost(
            "/{slug}/state/init",
            async (HttpRequest req, string slug, GameStore games, DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);

                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }

                var playerId = identity.PlayerId!.Value;
                var shortId = await ResolvePlayerShortIdAsync(devices, identity, playerId);

                if (string.IsNullOrWhiteSpace(shortId))
                    return Results.BadRequest(new { error = "Missing player short id." });

                var definition = await games.TryGetAsync(slug);
                if (definition is not GameDefinition game)
                    return Results.Json(
                        new { error = $"Game definition '{slug}' not found." },
                        statusCode: StatusCodes.Status500InternalServerError);

                var initialState = BuildInitialState(game, shortId);
                var persisted = await games.UpsertStateAsync(playerId, game, initialState);

                return Results.Json(new GameStateResponse(
                    game.Slug,
                    persisted.GameStateId,
                    persisted.State.Clone()));
            });

        group.MapPost(
            "/{slug}/state/load",
            async (HttpRequest req, string slug, GameStore store, DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(
                        req,
                        devices);

                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player." });
                }

                if (identity.PlayerId is null)
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or playerId/player_id_short in body." });

                var gameState = await store.LoadAsync(identity.PlayerId.Value, slug);
                if (gameState is null)
                    return Results.NotFound(new { error = "Game not found." });

                return Results.Json(new LoadGameStateResponse(
                    new GameDefinitionResponse(
                        gameState.Definition.GameId,
                        gameState.Definition.Slug,
                        gameState.Definition.DisplayName,
                        JsonSerializer.Deserialize<JsonElement>(gameState.Definition.DefaultStateJson).Clone()),
                    gameState.State.Clone(),
                    gameState.Created));
            });

        group.MapPost(
            "/{slug}/state/{gameStateId:guid}",
            async (HttpRequest req, string slug, Guid gameStateId, GameStore games, DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
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
                if (updated is not { } result || !string.Equals(result.Slug, slug, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { error = "Game state not found." });

                return Results.Json(new GameStateResponse(
                    result.Slug,
                    result.GameStateId,
                    result.State.Clone()));
            });

        group.MapPost(
            "/{slug}/state/merge/{gameStateId:guid}",
            async (HttpRequest req, Guid gameStateId, string slug, GameStore games, DeviceStore devices) =>
            {
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
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
                if (existing is not { } current || !string.Equals(current.Slug, slug, StringComparison.OrdinalIgnoreCase))
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


                var mergedState = JsonSerializer.SerializeToElement(baseState);
                var updated = await games.UpdateStateAsync(playerId, gameStateId, mergedState);
                if (updated is not { } result || !string.Equals(result.Slug, slug, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { error = "Game state not found." });

                return Results.Json(new GameStateResponse(
                    result.Slug,
                    result.GameStateId,
                    result.State.Clone()));
            });
    }

    private static async Task<string?> ResolvePlayerShortIdAsync(DeviceStore devices, RequestIdentity identity, Guid playerId)
    {
        if (!string.IsNullOrWhiteSpace(identity.PlayerShortId))
            return identity.PlayerShortId;

        var context = await devices.TryGetAsync(playerId.ToString(), identity.DeviceId);
        return context?.PlayerShortId;
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

        state = JsonSerializer.SerializeToElement(obj);
        return true;
    }

}
