using System.Text.Json;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Messaging;

internal static class TelegramMessagingEndpoints
{
    public static void MapTelegramMessagingEndpoints(this WebApplication application)
    {
        application.MapPost("/api/bots/{botId}/telegram-message", SendAsync);
        application.MapPost("/api/bots/telegram-message-all", SendAllAsync);
    }

    private static async Task<IResult> SendAsync(
        string botId, HttpContext context, TelegramMessagingService service)
    {
        MessageRequest? request = await ReadAsync(context);
        SessionTokenPayload session = Session(context);
        TelegramDeliveryResult result = await service.SendAsync(
            botId, request?.Message, session.Username, Remote(context), CancellationToken.None);
        return Results.Json(result, statusCode: Status(result.Outcome));
    }

    private static async Task<IResult> SendAllAsync(HttpContext context, TelegramMessagingService service)
    {
        MessageRequest? request = await ReadAsync(context);
        SessionTokenPayload session = Session(context);
        TelegramBroadcastResult result = await service.SendAllAsync(
            request?.Message, session.Username, Remote(context), CancellationToken.None);
        return Results.Json(result, statusCode: result.Outcome == "invalid_request" ? 400 : 200);
    }

    private static async Task<MessageRequest?> ReadAsync(HttpContext context)
    {
        try { return await context.Request.ReadFromJsonAsync<MessageRequest>(context.RequestAborted); }
        catch (JsonException) { return null; }
    }

    private static int Status(string outcome) => outcome switch
    {
        "succeeded" => 200,
        "invalid_request" => 400,
        "bot_not_found" => 404,
        "bot_disabled" => 409,
        _ => 502
    };

    private static SessionTokenPayload Session(HttpContext context) =>
        (SessionTokenPayload)context.Items[AuthenticationEndpoints.SessionItemKey]!;
    private static string Remote(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private sealed record MessageRequest(string? Message);
}
