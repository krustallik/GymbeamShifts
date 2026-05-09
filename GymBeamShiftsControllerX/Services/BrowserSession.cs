using System;
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

        public ChromeDriver Driver { get; private set; }

        public BrowserSession(AppConfig config)
        {
            _config = config;
        }

        public void InitializeDriver()
        {
            var options = new ChromeOptions();

            if (_config.Browser.Headless)
            {
                options.AddArgument("--headless=new");
            }

            options.AddArgument($"--window-size={_config.Browser.WindowSize}");

            if (_config.Browser.DisableGpu)
            {
                options.AddArgument("--disable-gpu");
            }

            var service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;

            Driver = new ChromeDriver(service, options);
            var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));

            Driver.Navigate().GoToUrl(_config.Auth.LoginUrl);
            Logger.Log("Открыта страница логина.");

            wait.Until(ExpectedConditions.ElementIsVisible(By.Name(AppConstants.LoginFieldName)));
            wait.Until(ExpectedConditions.ElementIsVisible(By.Name(AppConstants.PasswordFieldName)));

            Driver.FindElement(By.Name(AppConstants.LoginFieldName)).SendKeys(_config.Auth.Login);
            Driver.FindElement(By.Name(AppConstants.PasswordFieldName)).SendKeys(_config.Auth.Password);
            Logger.Log("Введены логин и пароль.");

            Logger.Log("Нажимаем кнопку логина.");
            Driver.FindElement(By.CssSelector(AppConstants.SubmitButtonSelector)).Click();
            Logger.Log("Кнопка логина нажата.");

            wait.Until(d => d.Url.Contains(_config.Auth.SuccessUrlContains));
            Logger.Log("Логин успешный!");
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