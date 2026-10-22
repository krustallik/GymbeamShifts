using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("Selenium")]
public sealed class AdminWebUiValidationTests
{
    [Fact]
    public void BotAdminUi_IsUkrainianHasHelpForEverySettingAndIncludesCollapsibleLogs()
    {
        string html = (string)ReflectionTestHelper
            .GetStaticMethod(typeof(AdminWebServer), "BuildAdminHtml")
            .Invoke(null, null)!;

        Assert.Contains("lang='uk'", html);
        Assert.Contains("Правила вибору змін", html);
        Assert.Contains("Мінімум годин до початку зміни", html);
        Assert.Contains("Пріоритетні типи змін", html);
        Assert.Contains("Дати й час вибраних змін", html);
        Assert.Contains("Застосовувати у вихідні та свята", html);
        Assert.DoesNotContain("class='time-scope-title'", html);
        Assert.DoesNotContain("id='startTimeScopeHelp'", html);
        Assert.Contains("id='startTimeScopeOptions'", html);
        Assert.Contains("13:30–14:30", html);
        Assert.Contains("<label for='startTimesToSkip'>Час початку", html);
        Assert.Contains("<label for='holidays'>Святкові дати", html);
        Assert.Contains("±30 хв", html);
        Assert.Contains("oninput='renderStartTimeScopeOptions()'", html);
        Assert.True(Count(html, "class='help'") >= 12);
        Assert.DoesNotContain("Today Logs", html);
        Assert.DoesNotContain("Refresh Logs", html);
        Assert.Contains("id='logs'", html);
        Assert.Contains("loadLogs()", html);
        Assert.Contains("id='settingsButton'", html);
        Assert.Contains("id='settingsDialog'", html);
        Assert.Contains("/api/user-credentials", html);
        Assert.Contains("id='gymBeamLogin'", html);
        Assert.Contains("id='telegramBotToken'", html);
        Assert.Contains("@BotFather", html);
        Assert.Contains("/newbot", html);
        Assert.Contains("/start", html);
        Assert.Contains("getUpdates", html);
        Assert.Contains("https://api.telegram.org/botВАШ_ТОКЕН/getUpdates", html);
        Assert.Contains("result → message → chat → id", html);
        Assert.DoesNotContain("@userinfobot", html);
        Assert.Contains("id='credentialValidationOverlay'", html);
        Assert.Contains("class='spinner'", html);
        Assert.Contains("Перевіряємо ваші дані", html);
        Assert.Contains("overlay.hidden = false", html);
        Assert.Contains("overlay.hidden = true", html);
        Assert.True(
            html.IndexOf("id='credentialValidationOverlay'", StringComparison.Ordinal)
            < html.IndexOf("</dialog>", StringComparison.Ordinal));
        Assert.Contains("credentialsValidationInProgress", html);
        Assert.Contains("event.preventDefault()", html);
        Assert.Contains(".validation-overlay { position:absolute", html);
        Assert.Contains("max-height:calc(100vh - 24px)", html);
        Assert.Contains("overflow-wrap:anywhere", html);
        Assert.Contains("font-size:16px", html);
        Assert.Contains("font-size:15px", html);
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
                document.getElementById('targetShiftDateTimes').value = '2026-08-25 08:00\n2026-08-26 09:30';
                document.getElementById('includedWeekdays').value = 'Monday\nFriday';
                document.getElementById('startTimesToSkip').value = '22:00\n21:45';
                document.getElementById('preferredShiftTypes').value = 'Test User';
                document.getElementById('holidays').value = '2028-02-29';
                document.getElementById('excludedDates').value = '2026-12-24';
                document.getElementById('startTimesToSkip').dispatchEvent(new Event('input', { bubbles: true }));
                document.querySelector("input[data-start-time='22:00']").click();
                """);

            js.ExecuteScript("""
                document.getElementById('preferredShiftTypes').value = 'CTP2 Export\nRAS Výroba';
                document.getElementById('excludedShiftTypes').value = 'Olepovanie B2B';
                document.getElementById('preferredLocations').value = 'Rastislavova';
                document.getElementById('excludedLocations').value = 'CTP2 - whs - letisko';
                """);
            Assert.Equal("CTP2 Export,RAS Výroba", js.ExecuteScript("return buildRulesPayload().preferredShiftTypes.join(',')"));
            Assert.Equal("Olepovanie B2B", js.ExecuteScript("return buildRulesPayload().excludedShiftTypes[0]"));
            Assert.Equal("Rastislavova", js.ExecuteScript("return buildRulesPayload().preferredLocations[0]"));
            Assert.Equal("CTP2 - whs - letisko", js.ExecuteScript("return buildRulesPayload().excludedLocations[0]"));
            Assert.Empty(driver.FindElements(By.Id("favoriteShiftUsers")));
            js.ExecuteScript("document.getElementById('app').classList.remove('hidden');document.getElementById('loginCard').classList.add('hidden');");
            foreach (int width in new[] { 1440, 390 })
            {
                driver.Manage().Window.Size = new System.Drawing.Size(width, 1000);
                var ratio = Convert.ToDouble(js.ExecuteScript("return document.querySelector('.container').getBoundingClientRect().width / innerWidth"));
                Assert.InRange(ratio, .90, .95);
                Assert.True((bool)js.ExecuteScript("return document.documentElement.scrollWidth <= innerWidth")!);
                Assert.Equal(width > 700 ? "2" : "1", js.ExecuteScript("return String(getComputedStyle(document.querySelector('.preferences-grid')).gridTemplateColumns.split(' ').length)"));
            }
            var validPayload = js.ExecuteScript("return buildRulesPayload();");
            Assert.NotNull(validPayload);
            Assert.Equal(
                "2026-08-25T08:00,2026-08-26T09:30",
                js.ExecuteScript("return buildRulesPayload().targetShiftDateTimes.join(',');"));
            Assert.Equal(
                2L,
                js.ExecuteScript(
                    "return document.querySelectorAll('#startTimeScopeOptions input[type=checkbox]').length;"));
            Assert.Equal(
                "22:00",
                js.ExecuteScript(
                    "return buildRulesPayload().startTimesToSkipOnWeekendsAndHolidays.join(',');"));

            js.ExecuteScript("""
                document.getElementById('startTimesToSkip').value = '22:00';
                document.getElementById('startTimesToSkip').dispatchEvent(new Event('input', { bubbles: true }));
                """);
            Assert.Equal(
                1L,
                js.ExecuteScript(
                    "return document.querySelectorAll('#startTimeScopeOptions input[type=checkbox]').length;"));
            Assert.Equal(
                "22:00",
                js.ExecuteScript(
                    "return buildRulesPayload().startTimesToSkipOnWeekendsAndHolidays.join(',');"));

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

            AssertValidationFails(
                js,
                "document.getElementById('targetShiftDateTimes').value = 'not-a-date'; return buildRulesPayload();",
                "Дати й час вибраних змін");
            js.ExecuteScript("document.getElementById('targetShiftDateTimes').value = '';");

            js.ExecuteScript(
                "document.getElementById('preferredShiftTypes').value = arguments[0];",
                new string('x', 201));
            AssertValidationFails(js, "return buildRulesPayload();", "Пріоритетні типи змін");
        }
        finally
        {
            try { driver?.Quit(); } catch { }
            driver?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WeekdaySettingsUi_OnlyEnabledDaysInheritUntilCustomizedAndKeepIndependentEmptyLists()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gymbeam-weekdays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = new ChromeOptions(); options.AddArgument("--headless=new"); options.AddArgument("--no-sandbox");
        using var driver = new ChromeDriver(options);
        try
        {
            string html = (string)ReflectionTestHelper.GetStaticMethod(typeof(AdminWebServer), "BuildAdminHtml").Invoke(null, null)!;
            string path = Path.Combine(directory, "admin.html"); File.WriteAllText(path, html);
            driver.Navigate().GoToUrl(new Uri(path).AbsoluteUri);
            driver.ExecuteScript("""
                document.getElementById('app').classList.remove('hidden');
                document.getElementById('loginCard').classList.add('hidden');
                document.getElementById('includedWeekdays').value = 'Thursday\nFriday';
                document.getElementById('startTimesToSkip').value = '14:00\n22:00';
                renderWeekdaySettings();
                """);
            Assert.Equal(2, driver.FindElements(By.CssSelector("#weekdayTabs button")).Count);
            Assert.True(driver.FindElement(By.Id("weekdayTimes")).GetAttribute("disabled") != null);
            Assert.Contains("22:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.ExecuteScript("document.getElementById('startTimesToSkip').value='08:00';renderStartTimeScopeOptions();");
            Assert.Equal("08:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.FindElement(By.Id("weekdayUseBase")).Click();
            Assert.Equal("08:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            var own = driver.FindElement(By.Id("weekdayTimes")); own.Clear(); own.SendKeys("06:00");
            driver.ExecuteScript("document.getElementById('startTimesToSkip').value='22:00';renderStartTimeScopeOptions();");
            Assert.Equal("06:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.FindElement(By.Id("weekdayTab-Friday")).Click();
            Assert.Equal("22:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.FindElement(By.Id("weekdayUseBase")).Click();
            driver.FindElement(By.Id("weekdayTimes")).SendKeys(Keys.Control + "a" + Keys.Backspace);
            Assert.Equal("[]", driver.ExecuteScript("return JSON.stringify(weekdayStartTimesToSkip.Friday)"));
            driver.ExecuteScript("document.getElementById('startTimesToSkip').value='14:00';renderStartTimeScopeOptions();");
            Assert.Equal("", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.ExecuteScript("document.getElementById('includedWeekdays').value='Thursday';renderWeekdaySettings();");
            Assert.Single(driver.FindElements(By.CssSelector("#weekdayTabs button")));
            Assert.Equal("06:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.ExecuteScript("document.getElementById('includedWeekdays').value='Thursday\\nFriday';renderWeekdaySettings();");
            driver.FindElement(By.Id("weekdayTab-Friday")).Click();
            Assert.False(driver.FindElement(By.Id("weekdayUseBase")).Selected);
            Assert.Equal("", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            driver.FindElement(By.Id("weekdayUseBase")).Click();
            Assert.Equal("14:00", driver.FindElement(By.Id("weekdayTimes")).GetAttribute("value"));
            Assert.Equal("false", driver.ExecuteScript("return String(Object.hasOwn(weekdayStartTimesToSkip,'Friday'))"));
            driver.FindElement(By.Id("weekdayTab-Friday")).SendKeys(Keys.ArrowLeft);
            Assert.Equal("true", driver.FindElement(By.Id("weekdayTab-Thursday")).GetAttribute("aria-selected"));
            foreach (int width in new[] { 1440, 390 })
            {
                driver.Manage().Window.Size = new System.Drawing.Size(width, 900);
                Assert.True((bool)driver.ExecuteScript("return document.documentElement.scrollWidth <= innerWidth")!);
            }
            driver.ExecuteScript("document.getElementById('includedWeekdays').value='Saturday\\nSunday';renderWeekdaySettings();");
            Assert.Empty(driver.FindElements(By.CssSelector("#weekdayTabs button")));
            Assert.Empty(driver.FindElements(By.Id("weekdayTimes")));
            Assert.Equal("06:00", driver.ExecuteScript("return weekdayStartTimesToSkip.Thursday[0]"));
        }
        finally
        {
            driver.Quit();
            Directory.Delete(directory, recursive: true);
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
