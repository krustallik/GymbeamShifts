namespace GymBeam.AdminManager.Dashboard;

public sealed record BotDashboardItem(
    string Id,
    string DisplayName,
    bool Enabled,
    string State,
    string Health,
    TimeSpan? Uptime,
    DateTimeOffset? LastUpdatedAtUtc,
    string Availability);
