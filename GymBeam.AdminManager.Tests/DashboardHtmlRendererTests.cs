using GymBeam.AdminManager.Dashboard;

namespace GymBeam.AdminManager.Tests;

public class DashboardHtmlRendererTests
{
    [Fact]
    public void Render_EncodesAllRegistryAndRuntimeValues()
    {
        var item = new BotDashboardItem(
            "bot1<script>alert(1)</script>",
            "<img src=x onerror=alert(1)>",
            Enabled: true,
            State: "running<script>",
            Health: "healthy&unsafe",
            Uptime: TimeSpan.FromMinutes(90),
            LastUpdatedAtUtc: new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            Availability: "available");

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("healthy&amp;unsafe", html);
    }

    [Fact]
    public void Render_UsesUnknownForNullRuntimeValues()
    {
        var item = new BotDashboardItem(
            "bot1",
            "Bot One",
            Enabled: true,
            State: "unknown",
            Health: "unknown",
            Uptime: null,
            LastUpdatedAtUtc: null,
            Availability: "unknown");

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains(">unknown<", html);
    }

    [Fact]
    public void Render_ProvidesExplicitLifecycleActionsAndUsesTextContentForResults()
    {
        var item = new BotDashboardItem(
            "bot1",
            "Bot One",
            Enabled: true,
            State: "running",
            Health: "healthy",
            Uptime: TimeSpan.FromMinutes(1),
            LastUpdatedAtUtc: DateTimeOffset.UtcNow,
            Availability: "available");

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains("data-action=\"start\"", html);
        Assert.Contains("data-action=\"stop\"", html);
        Assert.Contains("data-action=\"restart\"", html);
        Assert.Contains("data-action=\"logs\"", html);
        Assert.Contains("class=\"bot-logs\"", html);
        Assert.Contains("textContent", html);
        Assert.DoesNotContain("innerHTML", html);
    }

    [Fact]
    public void Render_CredentialFormContainsOnlyBlankInputsAndNoExistingSecretPlaceholders()
    {
        var item = new BotDashboardItem(
            "bot1",
            "Bot One",
            Enabled: true,
            State: "running",
            Health: "healthy",
            Uptime: null,
            LastUpdatedAtUtc: null,
            Availability: "available");

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains("class=\"credential-form\"", html);
        Assert.Contains("name=\"gymBeamPassword\"", html);
        Assert.Contains("name=\"telegramBotToken\"", html);
        Assert.Contains("name=\"botAdminTokenSecret\"", html);
        Assert.Contains("Blank fields remain unchanged", html);
        Assert.DoesNotContain("value=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder=\"existing", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_ProvidesProvisionEnableDisableAndExplicitDeleteControlsWithoutSecrets()
    {
        string html = DashboardHtmlRenderer.Render([]);

        Assert.Contains("class=\"provision-form\"", html);
        Assert.Contains("name=\"telegramToken\"", html);
        Assert.Contains("data-action=\"enable\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("data-action=\"disable\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("data-action=\"delete\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("DELETE ", DashboardHtmlRenderer.Render([Item()]));
        Assert.DoesNotContain("value=", html, StringComparison.OrdinalIgnoreCase);
    }

    private static BotDashboardItem Item() => new(
        "bot1", "Bot One", true, "running", "healthy", null, null, "available");
}
