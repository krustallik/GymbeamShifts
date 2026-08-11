using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("Selenium")]
public sealed class AdminWebUiValidationTests
{
    [Fact]
    public void BotAdminUi_IsUkrainianHasHelpForEverySettingAndOmitsTodayLogs()
    {
        string html = (string)ReflectionTestHelper
            .GetStaticMethod(typeof(AdminWebServer), "BuildAdminHtml")
            .Invoke(null, null)!;

        Assert.Contains("lang='uk'", html);
        Assert.Contains("Правила вибору змін", html);
        Assert.Contains("Мінімум годин до початку зміни", html);
        Assert.Contains("Пріоритетні працівники", html);
        Assert.True(Count(html, "class='help'") >= 12);
        Assert.DoesNotContain("Today Logs", html);
        Assert.DoesNotContain("Refresh Logs", html);
        Assert.DoesNotContain("id='logs'", html);
        Assert.DoesNotContain("loadLogs()", html);
        Assert.Contains("id='settingsButton'", html);
        Assert.Contains("id='settingsDialog'", html);
        Assert.Contains("/api/user-credentials", html);
        Assert.Contains("id='gymBeamLogin'", html);
        Assert.Contains("id='telegramBotToken'", html);
        Assert.Contains("@BotFather", html);
        Assert.Contains("/newbot", html);
        Assert.Contains("/start", html);
        Assert.Contains("@userinfobot", html);
        Assert.Contains("getUpdates", html);
        Assert.Contains("id='credentialValidationOverlay'", html);
        Assert.Contains("class='spinner'", html);
        Assert.Contains("Перевіряємо ваші дані", html);
        Assert.Contains("overlay.hidden = false", html);
        Assert.Contains("overlay.hidden = true", html);
        Assert.Contains("position:fixed", html);
        Assert.Contains("max-height:calc(100vh - 24px)", html);
        Assert.Contains("overflow-wrap:anywhere", html);
    }

    [Fact]
    public void ShiftRulesForm_ValidatesAllEditableValueTypes()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"gymbeam-admin-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ChromeDriver? driver = null;

        try
        {
            string html = (string)ReflectionTestHelper
                .GetStaticMethod(typeof(AdminWebServer), "BuildAdminHtml")
                .Invoke(null, null)!;
            string path = Path.Combine(directory, "admin.html");
            File.WriteAllText(path, html);

            var options = new ChromeOptions();
            options.AddArgument("--headless=new");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-gpu");
            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            driver = new ChromeDriver(service, options);
            driver.Navigate().GoToUrl(new Uri(path).AbsoluteUri);
            var js = (IJavaScriptExecutor)driver;

            js.ExecuteScript("""
                document.getElementById('shiftMinHoursAhead').value = '48';
                document.getElementById('weekendOrHolidayMinHoursAhead').value = '28';
                document.getElementById('importantShiftNotificationCount').value = '4';
                document.getElementById('importantShiftNotificationDelayMilliseconds').value = '30000';
                document.getElementById('includedWeekdays').value = 'Monday\nFriday';
                document.getElementById('startTimesToSkip').value = '22:00\n21:45';
                document.getElementById('favoriteShiftUsers').value = 'Test User';
                document.getElementById('holidays').value = '2028-02-29';
                document.getElementById('excludedDates').value = '2026-12-24';
                """);

            var validPayload = js.ExecuteScript("return buildRulesPayload();");
            Assert.NotNull(validPayload);

            AssertValidationFails(
                js,
                "document.getElementById('includedWeekdays').value = 'Funday'; return buildRulesPayload();",
                "Дозволені дні тижня");
            js.ExecuteScript("document.getElementById('includedWeekdays').value = 'Monday';");

            AssertValidationFails(
                js,
                "document.getElementById('startTimesToSkip').value = '25:00'; return buildRulesPayload();",
                "Час початку, який треба пропускати");
            js.ExecuteScript("document.getElementById('startTimesToSkip').value = '22:00';");

            AssertValidationFails(
                js,
                "document.getElementById('holidays').value = '2026-02-30'; return buildRulesPayload();",
                "Святкові дати");
            js.ExecuteScript("document.getElementById('holidays').value = '2026-02-28';");

            AssertValidationFails(
                js,
                "document.getElementById('shiftMinHoursAhead').value = '1.5'; return buildRulesPayload();",
                "Мінімум годин до початку зміни");
            js.ExecuteScript("document.getElementById('shiftMinHoursAhead').value = '48';");

            js.ExecuteScript(
                "document.getElementById('favoriteShiftUsers').value = arguments[0];",
                new string('x', 101));
            AssertValidationFails(js, "return buildRulesPayload();", "Пріоритетні працівники");
        }
        finally
        {
            try { driver?.Quit(); } catch { }
            driver?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertValidationFails(
        IJavaScriptExecutor js,
        string script,
        string expectedMessage)
    {
        var exception = Assert.ThrowsAny<WebDriverException>(() => js.ExecuteScript(script));
        Assert.Contains(expectedMessage, exception.Message);
    }

    private static int Count(string value, string text)
    {
        int count = 0;
        for (int index = 0; (index = value.IndexOf(text, index, StringComparison.Ordinal)) >= 0; index += text.Length)
        {
            count++;
        }
        return count;
    }
}
