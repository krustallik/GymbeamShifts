using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Logs;

internal static class BotLogEndpoints
{
    public static void MapBotLogEndpoints(this WebApplication application)
    {
        application.MapGet("/api/bots/{botId}/logs", async (
            string botId,
            int? tail,
            HttpContext context,
            BotLogService service) =>
        {
            var session = (SessionTokenPayload)context.Items[AuthenticationEndpoints.SessionItemKey]!;
            BotLogView result = await service.ReadAsync(
                botId,
                tail ?? 200,
                session.Username,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.RequestAborted);
            return Results.Json(result, statusCode: StatusCode(result.Outcome));
        });
    }

    private static int StatusCode(string outcome) => outcome switch
    {
        "succeeded" => StatusCodes.Status200OK,
        "invalid_request" => StatusCodes.Status400BadRequest,
        "bot_not_found" or "container_not_found" => StatusCodes.Status404NotFound,
        "docker_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "docker_timeout" => StatusCodes.Status504GatewayTimeout,
        "response_too_large" => StatusCodes.Status413PayloadTooLarge,
        _ => StatusCodes.Status409Conflict
    };
}
