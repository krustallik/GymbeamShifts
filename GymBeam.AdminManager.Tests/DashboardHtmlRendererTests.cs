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
    public void Render_DisplaysRuntimeTimestampInBratislavaTime()
    {
        var item = new BotDashboardItem(
            "bot1",
            "Bot One",
            Enabled: true,
            State: "running",
            Health: "healthy",
            Uptime: null,
            LastUpdatedAtUtc: new DateTimeOffset(2026, 8, 11, 15, 16, 0, TimeSpan.Zero),
            Availability: "available");

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains("2026-08-11 17:16:00", html);
        Assert.DoesNotContain("15:16:00 UTC", html);
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
        Assert.Contains("button.dataset.action!=='logs'&&response.ok", html);
        Assert.Contains("setTimeout(()=>location.reload(),300)", html);
        Assert.Contains("No logs yet.", html);
        Assert.Contains("id=\"loading-overlay\"", html);
        Assert.Contains("class=\"loading-spinner\"", html);
        Assert.Contains("function showLoading", html);
        Assert.Contains("function hideLoading", html);
        Assert.Contains("Updating bot", html);
        Assert.Contains("Loading logs", html);
        Assert.Contains("Creating bot", html);
        Assert.Contains("Updating credentials", html);
        Assert.Contains("Sending message", html);
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
        Assert.DoesNotContain("name=\"gymBeamPassword\"", html);
        Assert.DoesNotContain("name=\"telegramBotToken\"", html);
        Assert.Contains("name=\"botAdminTokenSecret\"", html);
        Assert.Contains("Blank fields remain unchanged", html);
        Assert.DoesNotContain(" value=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder=\"existing", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_ProvidesProvisionEnableDisableAndExplicitDeleteControlsWithoutSecrets()
    {
        string html = DashboardHtmlRenderer.Render([]);

        Assert.Contains("class=\"provision-form\"", html);
        Assert.DoesNotContain("name=\"telegramToken\"", html);
        Assert.DoesNotContain("name=\"gymBeamLogin\"", html);
        Assert.Contains("name=\"botAdminUsername\"", html);
        Assert.Contains("Lowercase letters, numbers and hyphens only", html);
        Assert.Contains("Enter only the prefix, not the full address", html);
        Assert.Contains("openssl rand -base64 32", html);
        Assert.Contains("input.value.trim().toLowerCase()", html);
        Assert.Contains("toLowerCase().replace(/_/g,'-')", html);
        Assert.Contains("do not use spaces or underscores", html);
        Assert.Contains("Some fields are invalid", html);
        Assert.Contains("data-action=\"enable\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("data-action=\"disable\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("data-action=\"delete\"", DashboardHtmlRenderer.Render([Item()]));
        Assert.Contains("DELETE ", DashboardHtmlRenderer.Render([Item()]));
        Assert.DoesNotContain(" value=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_ProvidesPerBotAndBroadcastTelegramMessageControls()
    {
        string html = DashboardHtmlRenderer.Render([Item()]);

        Assert.Contains("data-message-bot=\"bot1\"", html);
        Assert.Contains("id=\"message-all\"", html);
        Assert.Contains("id=\"message-dialog\"", html);
        Assert.Contains("/telegram-message", html);
        Assert.Contains("/api/bots/telegram-message-all", html);
    }

    [Fact]
    public void Render_ShowsStorageUsageAndHighlightsLargeBot()
    {
        BotDashboardItem item = Item() with { StorageBytes = 151L * 1024 * 1024 };

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains("Storage: 151.0 MB", html);
        Assert.Contains("metric-value warning", html);
        Assert.Contains("<th>Storage</th>", html);
    }

    [Fact]
    public void Render_ShowsCurrentRamForEachBotAndTotal()
    {
        BotDashboardItem item = Item() with { MemoryBytes = 128L * 1024 * 1024 };

        string html = DashboardHtmlRenderer.Render([item]);

        Assert.Contains("RAM: 128.0 MB", html);
        Assert.Contains("<th>RAM</th>", html);
        Assert.Contains(">128.0 MB</span>", html);
    }

    private static BotDashboardItem Item() => new(
        "bot1", "Bot One", true, "running", "healthy", null, null, "available");
}
