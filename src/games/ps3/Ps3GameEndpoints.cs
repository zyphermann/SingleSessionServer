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

        group.MapPost(
            "/game/{gameStateId:guid}/start",
            async (HttpRequest req, Guid gameStateId, GameStore games, DeviceStore devices) =>
            {
                // TODO: implement game logic with MPT and maths here
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }
                throw new NotImplementedException();
            });

        group.MapPost(
            "/game/{gameStateId:guid}/next",
            async (HttpRequest req, Guid gameStateId, GameStore games, DeviceStore devices) =>
            {
                // TODO: implement game logic with MPT and maths here
                RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
                if (identity is null)
                {
                    return Results.BadRequest(new { error = "Unknown player. Provide player_id cookie or X-Player-Id/X-Player-Short-Id headers." });
                }
                throw new NotImplementedException();
            });
    }
}

sealed record Ps3StateResponse(string Game, Guid GameStateId, JsonElement State);
