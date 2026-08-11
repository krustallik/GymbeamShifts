using System;
using System.Collections.Generic;
using System.IO;
using GymBeamShiftsControllerX.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;

namespace GymBeamShiftsControllerX.Services
{
    public class BrowserSession
    {
        private readonly AppConfig _config;
        private readonly int _remoteDebuggingPort;

        public ChromeDriver Driver { get; private set; }

        public BrowserSession(AppConfig config, int remoteDebuggingPort = 9222)
        {
            _config = config;
            _remoteDebuggingPort = remoteDebuggingPort;
        }

        public void InitializeDriver()
        {
            var options = new ChromeOptions();
            string chromeBinary = Environment.GetEnvironmentVariable("CHROME_BIN") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(chromeBinary))
            {
                options.BinaryLocation = chromeBinary;
            }

            bool runningInContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            bool useHeadless = _config.Browser.Headless || runningInContainer;
            if (useHeadless)
            {
                options.AddArgument("--headless=new");
            }

            Logger.Log($"Chrome start mode: {(useHeadless ? "headless" : "headed")}");

            options.AddArgument($"--window-size={_config.Browser.WindowSize}");

            if (_config.Browser.DisableGpu)
            {
                options.AddArgument("--disable-gpu");
            }

            // Required for stable Chrome execution in Linux containers.
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-software-rasterizer");
            options.AddArgument("--disable-extensions");
            options.AddArgument("--disable-background-networking");
            options.AddArgument($"--remote-debugging-port={_remoteDebuggingPort}");
            options.AddArgument("--disable-blink-features=AutomationControlled");

            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;

            Driver = new ChromeDriver(service, options);
            ConfigureBrowserIdentity();

            try
            {
                var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));

                Driver.Navigate().GoToUrl(_config.Auth.LoginUrl);
                wait.Until(d =>
                    ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() == "complete");
                Logger.Log($"Открыта страница логина. URL: {Driver.Url}; title: {Driver.Title}");

                DismissCookieBanner();

                IWebElement loginField = wait.Until(
                    ExpectedConditions.ElementIsVisible(By.CssSelector("#login, input[name='login']")));
                IWebElement passwordField = wait.Until(
                    ExpectedConditions.ElementIsVisible(By.CssSelector("#password, input[name='password']")));

                loginField.SendKeys(_config.Auth.Login);
                passwordField.SendKeys(_config.Auth.Password);
                Logger.Log("Введены логин и пароль.");

                Logger.Log("Нажимаем кнопку логина.");
                Driver.FindElement(By.CssSelector(AppConstants.SubmitButtonSelector)).Click();
                Logger.Log("Кнопка логина нажата.");

                wait.Until(d => d.Url.Contains(_config.Auth.SuccessUrlContains));
                Logger.Log("Логин успешный!");
            }
            catch (Exception ex) when (ex is WebDriverTimeoutException or NoSuchElementException)
            {
                SaveDiagnostics(ex);
                throw;
            }
        }

        private void ConfigureBrowserIdentity()
        {
            string browserVersion =
                Driver.Capabilities.GetCapability("browserVersion")?.ToString() ?? "149.0.0.0";
            string userAgent =
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 " +
                $"(KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36";

            Driver.ExecuteCdpCommand(
                "Network.setUserAgentOverride",
                new Dictionary<string, object> { ["userAgent"] = userAgent });
            Driver.ExecuteCdpCommand(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object>
                {
                    ["source"] =
                        "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });"
                });
        }

        private void DismissCookieBanner()
        {
            try
            {
                var cookieWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(5));
                IWebElement button = cookieWait.Until(
                    ExpectedConditions.ElementToBeClickable(By.Id(AppConstants.CookiesEssentialButtonId)));
                button.Click();
                Logger.Log("Cookie-баннер закрыт.");
            }
            catch (WebDriverTimeoutException)
            {
                Logger.Log("Cookie-баннер не показан.");
            }
        }

        private void SaveDiagnostics(Exception exception)
        {
            try
            {
                string configuredPath =
                    Environment.GetEnvironmentVariable("GYMBEAM_DIAGNOSTICS_PATH") ?? string.Empty;
                string logDirectory = Path.GetDirectoryName(Logger.GetLogFilePath()) ?? AppContext.BaseDirectory;
                string diagnosticsDirectory = string.IsNullOrWhiteSpace(configuredPath)
                    ? Path.Combine(logDirectory, "diagnostics")
                    : configuredPath;
                Directory.CreateDirectory(diagnosticsDirectory);

                string prefix = $"login-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
                string htmlPath = Path.Combine(diagnosticsDirectory, $"{prefix}.html");
                string screenshotPath = Path.Combine(diagnosticsDirectory, $"{prefix}.png");

                File.WriteAllText(htmlPath, Driver.PageSource);
                ((ITakesScreenshot)Driver).GetScreenshot().SaveAsFile(screenshotPath);

                Logger.Log(
                    $"Ошибка входа: {exception.Message}; URL: {Driver.Url}; title: {Driver.Title}; " +
                    $"диагностика: {htmlPath}, {screenshotPath}");
            }
            catch (Exception diagnosticsException)
            {
                Logger.Log($"Не удалось сохранить диагностику браузера: {diagnosticsException.Message}");
            }
        }

        public void Quit()
        {
            try
            {
                Driver?.Quit();
            }
            finally
            {
                Driver = null;
            }
        }
    }
}
