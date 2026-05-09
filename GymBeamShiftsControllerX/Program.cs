using System;
using System.Threading;
using GymBeamShiftsControllerX.Config;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;

namespace GymBeamShiftsControllerX
{
    public class Program
    {
        private static int iterationCount = 0;

        static void Main(string[] args)
        {
            AppConfig config;

            try
            {
                config = ConfigurationLoader.Load(AppConstants.ConfigFileName);
                Logger.Log("Конфигурация успешно загружена.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось загрузить конфигурацию: {ex.Message}");
                return;
            }

            var browserSession = new BrowserSession(config);
            var shiftChecker = new ShiftChecker(browserSession, config);

            browserSession.InitializeDriver();

            while (true)
            {
                try
                {
                    iterationCount++;
                    shiftChecker.CheckForShifts();
                }
                catch (WebDriverException ex) when (
                    ex.Message.Contains("invalid session id", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("not connected to DevTools", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"WebDriver упал, перезапуск: {ex.Message}");
                    try { browserSession.Quit(); } catch { }
                    browserSession.InitializeDriver();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Произошла ошибка: {ex.Message}");
                }

                if (iterationCount >= config.Timing.DriverRestartAfterIterations)
                {
                    Logger.Log($"Достигнуто {config.Timing.DriverRestartAfterIterations} итераций. Перезапускаем драйвер для освобождения памяти.");
                    try
                    {
                        browserSession.Quit();
                    }
                    catch
                    {
                    }

                    browserSession.InitializeDriver();
                    iterationCount = 0;
                }

                Thread.Sleep(config.Timing.CheckIntervalMinutes * 60 * 1000);
            }
        }
    }
}