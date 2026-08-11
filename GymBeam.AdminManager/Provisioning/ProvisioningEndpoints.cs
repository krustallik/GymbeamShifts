using System.Text.Json;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Provisioning;

internal static class ProvisioningEndpoints
{
    public static void MapProvisioningEndpoints(this WebApplication application)
    {
        application.MapPost("/api/bots/provision", ProvisionAsync);
        application.MapPost("/api/bots/{botId}/disable", (
            string botId, HttpContext context, BotAdministrationService service) =>
            ChangeStateAsync(botId, context, service.DisableAsync));
        application.MapPost("/api/bots/{botId}/enable", (
            string botId, HttpContext context, BotAdministrationService service) =>
            ChangeStateAsync(botId, context, service.EnableAsync));
        application.MapPost("/api/bots/{botId}/delete", DeleteAsync);
    }

    private static async Task<IResult> ProvisionAsync(
        HttpContext context,
        BotProvisioningService service)
    {
        ProvisionBotRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ProvisionBotRequest>(context.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return Results.BadRequest(new { outcome = "invalid_request" });
        }

        SessionTokenPayload session = Session(context);
        ProvisioningResult result = await service.ProvisionAsync(
            request,
            session.Username,
            Remote(context),
            CancellationToken.None);
        return Results.Json(result, statusCode: Status(result.Outcome));
    }

    private static async Task<IResult> DeleteAsync(
        string botId,
        HttpContext context,
        BotAdministrationService service)
    {
        DeleteBotRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<DeleteBotRequest>(context.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }

        SessionTokenPayload session = Session(context);
        BotAdministrationResult result = await service.DeleteAsync(
            botId,
            request?.Confirmation,
            session.Username,
            Remote(context),
            CancellationToken.None);
        return Results.Json(result, statusCode: Status(result.Outcome));
    }

    private static async Task<IResult> ChangeStateAsync(
        string botId,
        HttpContext context,
        Func<string, string?, string?, CancellationToken, Task<BotAdministrationResult>> action)
    {
        SessionTokenPayload session = Session(context);
        BotAdministrationResult result = await action(
            botId, session.Username, Remote(context), CancellationToken.None);
        return Results.Json(result, statusCode: Status(result.Outcome));
    }

    private static SessionTokenPayload Session(HttpContext context) =>
        (SessionTokenPayload)context.Items[AuthenticationEndpoints.SessionItemKey]!;

    private static string Remote(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static int Status(string outcome) => outcome switch
    {
        "succeeded" or "already_exists" or "already_enabled" or "already_disabled" => StatusCodes.Status200OK,
        "invalid_request" or "confirmation_required" => StatusCodes.Status400BadRequest,
        "bot_not_found" => StatusCodes.Status404NotFound,
        "docker_unavailable" or "caddy_unavailable" or "storage_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "docker_timeout" or "caddy_timeout" or "health_timeout" or "external_health_timeout" => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status409Conflict
    };

    private sealed record DeleteBotRequest(string? Confirmation);
}
