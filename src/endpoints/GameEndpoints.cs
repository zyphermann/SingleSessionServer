using System.Text.Json;

sealed record GameDefinitionRequest(string? DisplayName, JsonElement? DefaultState);

sealed record GameDefinitionResponse(Guid Id, string Slug, string DisplayName, JsonElement DefaultState);

sealed record GameStateResponse(GameDefinitionResponse Definition, JsonElement State, bool Created);

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

        group.MapGet("/{slug}", async (string slug, GameStore store) =>
        {
            var def = await store.TryGetAsync(slug);
            if (def is not GameDefinition definition)
                return Results.NotFound(new { error = "Game not found" });

            var json = JsonSerializer.Deserialize<JsonElement>(definition.DefaultStateJson);
            return Results.Json(new GameDefinitionResponse(definition.GameId, definition.Slug, definition.DisplayName, json.Clone()));
        });

        group.MapPut("/{slug}", async (string slug, GameDefinitionRequest request, GameStore store) =>
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

        group.MapPost("/{slug}/state/load", async (HttpRequest req, string slug, GameStore store) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var playerCookie) || !Guid.TryParse(playerCookie, out var playerId))
                return Results.BadRequest(new { error = "Missing or invalid player_id cookie. Call /device/init first." });

            var gameState = await store.LoadAsync(playerId, slug);
            if (gameState is null)
                return Results.NotFound(new { error = "Game not found." });

            return Results.Json(new GameStateResponse(
                new GameDefinitionResponse(
                    gameState.Definition.GameId,
                    gameState.Definition.Slug,
                    gameState.Definition.DisplayName,
                    JsonSerializer.Deserialize<JsonElement>(gameState.Definition.DefaultStateJson).Clone()),
                gameState.State.Clone(),
                gameState.Created));
        });
    }
}

