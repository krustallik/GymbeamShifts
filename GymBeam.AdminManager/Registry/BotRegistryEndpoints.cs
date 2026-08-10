using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using GymBeam.AdminManager.Dashboard;

namespace GymBeam.AdminManager.Registry;

internal static class BotRegistryEndpoints
{
    public static void MapBotRegistryEndpoints(this WebApplication application)
    {
        application.MapGet("/api/bots", async (
            DashboardService dashboard,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<BotDashboardItem> bots = await dashboard.GetAllAsync(cancellationToken);
            return Results.Json(bots);
        });
        application.MapGet("/", async (
            HttpContext context,
            DashboardService dashboard,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            IReadOnlyList<BotDashboardItem> bots = await dashboard.GetAllAsync(cancellationToken);
            return Results.Content(
                DashboardHtmlRenderer.Render(bots),
                "text/html; charset=utf-8");
        });
    }
}
