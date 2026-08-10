using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Lifecycle;

internal static class BotLifecycleEndpoints
{
    public static void MapBotLifecycleEndpoints(this WebApplication application)
    {
        application.MapPost("/api/bots/{botId}/start", (
            string botId,
            HttpContext context,
            BotLifecycleService service) => ExecuteAsync(botId, BotLifecycleAction.Start, context, service));
        application.MapPost("/api/bots/{botId}/stop", (
            string botId,
            HttpContext context,
            BotLifecycleService service) => ExecuteAsync(botId, BotLifecycleAction.Stop, context, service));
        application.MapPost("/api/bots/{botId}/restart", (
            string botId,
            HttpContext context,
            BotLifecycleService service) => ExecuteAsync(botId, BotLifecycleAction.Restart, context, service));
        application.MapGet("/api/bots/{botId}/operations/latest", (
            string botId,
            BotLifecycleService service) =>
        {
            BotLifecycleOperation? operation = service.GetProgress(botId);
            return operation is null ? Results.NotFound() : Results.Json(operation);
        });
    }

    private static async Task<IResult> ExecuteAsync(
        string botId,
        BotLifecycleAction action,
        HttpContext context,
        BotLifecycleService service)
    {
        var session = (SessionTokenPayload)context.Items[AuthenticationEndpoints.SessionItemKey]!;
        BotLifecycleOperation operation = await service.ExecuteAsync(
            botId,
            action,
            session.Username,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            CancellationToken.None);
        return Results.Json(operation, statusCode: StatusCode(operation.Outcome));
    }

    private static int StatusCode(string outcome) => outcome switch
    {
        "succeeded" or "already_running" or "already_stopped" => StatusCodes.Status200OK,
        "bot_not_found" or "container_not_found" => StatusCodes.Status404NotFound,
        "docker_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "docker_timeout" or "health_timeout" => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status409Conflict
    };
}
