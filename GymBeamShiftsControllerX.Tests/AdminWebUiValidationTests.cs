using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("Selenium")]
public sealed class AdminWebUiValidationTests
{
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
                "IncludedWeekdays");
            js.ExecuteScript("document.getElementById('includedWeekdays').value = 'Monday';");

            AssertValidationFails(
                js,
                "document.getElementById('startTimesToSkip').value = '25:00'; return buildRulesPayload();",
                "StartTimesToSkip");
            js.ExecuteScript("document.getElementById('startTimesToSkip').value = '22:00';");

            AssertValidationFails(
                js,
                "document.getElementById('holidays').value = '2026-02-30'; return buildRulesPayload();",
                "Holidays");
            js.ExecuteScript("document.getElementById('holidays').value = '2026-02-28';");

            AssertValidationFails(
                js,
                "document.getElementById('shiftMinHoursAhead').value = '1.5'; return buildRulesPayload();",
                "ShiftMinHoursAhead");
            js.ExecuteScript("document.getElementById('shiftMinHoursAhead').value = '48';");

            js.ExecuteScript(
                "document.getElementById('favoriteShiftUsers').value = arguments[0];",
                new string('x', 101));
            AssertValidationFails(js, "return buildRulesPayload();", "FavoriteShiftUsers");
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
}
