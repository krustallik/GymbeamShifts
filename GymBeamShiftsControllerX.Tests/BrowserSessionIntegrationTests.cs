using System.Reflection;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("Selenium")]
public sealed class BrowserSessionIntegrationTests
{
    [Fact]
    public void PrivateBrowserOperations_WorkAgainstLocalHeadlessPage()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"gymbeam-browser-private-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string pagePath = Path.Combine(directory, "page.html");
        File.WriteAllText(pagePath, """
            <!doctype html><html><body>
              <button id="cookies-consent-essential" onclick="this.remove()">Accept</button>
              <div id="content">diagnostic content</div>
            </body></html>
            """);

        ChromeDriver? driver = null;
        string? previousDiagnosticsPath = Environment.GetEnvironmentVariable("GYMBEAM_DIAGNOSTICS_PATH");
        try
        {
            var options = new ChromeOptions();
            options.AddArgument("--headless=new");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-gpu");
            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;
            driver = new ChromeDriver(service, options);
            driver.Navigate().GoToUrl(new Uri(pagePath).AbsoluteUri);

            var session = new BrowserSession(new AppConfig());
            SetDriver(session, driver);

            ReflectionTestHelper.GetInstanceMethod(typeof(BrowserSession), "ConfigureBrowserIdentity")
                .Invoke(session, null);
            driver.Navigate().Refresh();
            ReflectionTestHelper.GetInstanceMethod(typeof(BrowserSession), "DismissCookieBanner")
                .Invoke(session, null);

            Assert.Empty(driver.FindElements(By.Id("cookies-consent-essential")));
            Assert.NotEqual("true", ((IJavaScriptExecutor)driver)
                .ExecuteScript("return String(navigator.webdriver)")?.ToString());

            Environment.SetEnvironmentVariable("GYMBEAM_DIAGNOSTICS_PATH", directory);
            ReflectionTestHelper.GetInstanceMethod(typeof(BrowserSession), "SaveDiagnostics")
                .Invoke(session, new object[] { new Exception("test diagnostic") });

            Assert.Single(Directory.GetFiles(directory, "login-*.html"));
            Assert.Single(Directory.GetFiles(directory, "login-*.png"));
            Assert.Contains("diagnostic content", File.ReadAllText(Directory.GetFiles(directory, "login-*.html")[0]));

            string invalidDiagnosticsPath = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(invalidDiagnosticsPath, "file");
            Environment.SetEnvironmentVariable("GYMBEAM_DIAGNOSTICS_PATH", invalidDiagnosticsPath);
            var diagnosticsFailure = Record.Exception(() =>
                ReflectionTestHelper.GetInstanceMethod(typeof(BrowserSession), "SaveDiagnostics")
                    .Invoke(session, new object[] { new Exception("expected diagnostics failure") }));
            Assert.Null(diagnosticsFailure);

            driver.Navigate().GoToUrl("data:text/html,<html><body>No cookie banner</body></html>");
            var missingCookieBanner = Record.Exception(() =>
                ReflectionTestHelper.GetInstanceMethod(typeof(BrowserSession), "DismissCookieBanner")
                    .Invoke(session, null));
            Assert.Null(missingCookieBanner);

            session.Quit();
            driver.Dispose();
            driver = null;
            Assert.Null(session.Driver);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_DIAGNOSTICS_PATH", previousDiagnosticsPath);
            try { driver?.Quit(); } catch { }
            driver?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Quit_BeforeInitialization_DoesNotThrow()
    {
        var session = new BrowserSession(new AppConfig());

        var exception = Record.Exception(session.Quit);

        Assert.Null(exception);
        Assert.Null(session.Driver);
    }

    [Fact]
    public void CleanupDiagnostics_KeepsOnlyTwentyRecentSetsAndRemovesExpiredSets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gymbeam-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (int index = 0; index < 22; index++)
            {
                string prefix = Path.Combine(directory, $"login-20260811-1200{index:00}");
                File.WriteAllText(prefix + ".html", "html");
                File.WriteAllText(prefix + ".png", "png");
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-index);
                File.SetLastWriteTimeUtc(prefix + ".html", timestamp);
                File.SetLastWriteTimeUtc(prefix + ".png", timestamp);
            }
            string expired = Path.Combine(directory, "login-20260101-000000000");
            File.WriteAllText(expired + ".html", "old");
            File.WriteAllText(expired + ".png", "old");
            File.SetLastWriteTimeUtc(expired + ".html", DateTime.UtcNow.AddDays(-15));
            File.SetLastWriteTimeUtc(expired + ".png", DateTime.UtcNow.AddDays(-15));

            ReflectionTestHelper.GetStaticMethod(typeof(BrowserSession), "CleanupDiagnostics")
                .Invoke(null, new object[] { directory });

            Assert.Equal(20, Directory.GetFiles(directory, "login-*.html").Length);
            Assert.Equal(20, Directory.GetFiles(directory, "login-*.png").Length);
            Assert.False(File.Exists(expired + ".html"));
            Assert.False(File.Exists(expired + ".png"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void SetDriver(BrowserSession session, ChromeDriver driver)
    {
        var field = typeof(BrowserSession).GetField(
            "<Driver>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BrowserSession.Driver backing field was not found.");
        field.SetValue(session, driver);
    }
}
