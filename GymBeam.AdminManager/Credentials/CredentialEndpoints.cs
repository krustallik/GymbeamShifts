using System.Text.Json;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Credentials;

internal static class CredentialEndpoints
{
    public static void MapCredentialEndpoints(this WebApplication application)
    {
        application.MapPut("/api/bots/{botId}/credentials", UpdateAsync);
    }

    private static async Task<IResult> UpdateAsync(
        string botId,
        HttpContext context,
        CredentialUpdateService service)
    {
        CredentialUpdateRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<CredentialUpdateRequest>(
                context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid credential update" });
        }

        if (request is null)
        {
            return Results.BadRequest(new { error = "Invalid credential update" });
        }

        var session = (SessionTokenPayload)context.Items[AuthenticationEndpoints.SessionItemKey]!;
        CredentialUpdateResult result = await service.UpdateAsync(
            botId,
            request,
            session.Username,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            CancellationToken.None);
        return Results.Json(result, statusCode: StatusCode(result.Outcome));
    }

    private static int StatusCode(string outcome) => outcome switch
    {
        "succeeded" or "no_changes" => StatusCodes.Status200OK,
        "invalid_request" => StatusCodes.Status400BadRequest,
        "bot_not_found" => StatusCodes.Status404NotFound,
        "busy" => StatusCodes.Status409Conflict,
        "storage_failure" => StatusCodes.Status507InsufficientStorage,
        "rolled_back" => StatusCodes.Status502BadGateway,
        "rollback_failed" or "rollback_restart_failed" or "rollback_cleanup_failed" =>
            StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError
    };
}
