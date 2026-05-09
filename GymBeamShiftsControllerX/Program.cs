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
        private static long totalIterationCount = 0;
        private static long lastErrorNotificationIteration = -30;
        private static DateTime lastDailyStatusDate = DateTime.MinValue.Date;
        private static DateTime lastIterationAt = DateTime.MinValue;
        private static DateTime lastSuccessAt = DateTime.MinValue;
        private static DateTime lastErrorAt = DateTime.MinValue;
        private static string lastErrorMessage = string.Empty;
        private const int ErrorNotificationIntervalIterations = 30;
        private static readonly TimeSpan DailyStatusTime = new TimeSpan(10, 30, 0);

        static void Main(string[] args)
        {
            AppConfig config;
            var startTime = DateTime.Now;

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
            var adminWeb = new AdminWebServer(config, () => CreateStatusSnapshot(startTime, config));
            try
            {
                adminWeb.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Admin Web failed to start: {ex.Message}");
            }

            browserSession.InitializeDriver();

            while (true)
            {
                try
                {
                    iterationCount++;
                    totalIterationCount++;
                    lastIterationAt = DateTime.Now;
                    shiftChecker.CheckForShifts();
                    lastSuccessAt = DateTime.Now;
                }
                catch (WebDriverException ex) when (
                    ex.Message.Contains("invalid session id", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("not connected to DevTools", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"WebDriver упал, перезапуск: {ex.Message}");
                    lastErrorAt = DateTime.Now;
                    lastErrorMessage = ex.Message;
                    TrySendErrorNotification(config, ex, "WebDriver crash");
                    try { browserSession.Quit(); } catch { }
                    browserSession.InitializeDriver();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Произошла ошибка: {ex.Message}");
                    lastErrorAt = DateTime.Now;
                    lastErrorMessage = ex.Message;
                    TrySendErrorNotification(config, ex, "Runtime error");
                }

                TrySendDailyStatus(config, startTime);

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

        private static BotStatusSnapshot CreateStatusSnapshot(DateTime startTime, AppConfig config)
        {
            var now = DateTime.Now;
            TimeSpan maxLag = TimeSpan.FromMinutes((config.Timing.CheckIntervalMinutes * 2) + 1);
            bool isRunning = lastIterationAt != DateTime.MinValue && (now - lastIterationAt) <= maxLag;

            return new BotStatusSnapshot
            {
                IsRunning = isRunning,
                TotalIterations = totalIterationCount,
                LastIterationAt = lastIterationAt == DateTime.MinValue ? string.Empty : lastIterationAt.ToString("yyyy-MM-dd HH:mm:ss"),
                LastSuccessAt = lastSuccessAt == DateTime.MinValue ? string.Empty : lastSuccessAt.ToString("yyyy-MM-dd HH:mm:ss"),
                LastErrorAt = lastErrorAt == DateTime.MinValue ? string.Empty : lastErrorAt.ToString("yyyy-MM-dd HH:mm:ss"),
                LastErrorMessage = lastErrorMessage,
                Uptime = FormatUptime(now - startTime)
            };
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
        }

        private static void TrySendDailyStatus(AppConfig config, DateTime startTime)
        {
            var now = DateTime.Now;
            if (now.TimeOfDay < DailyStatusTime || lastDailyStatusDate == now.Date)
            {
                return;
            }

            var uptime = now - startTime;
            string message =
                "STATUS OK\n" +
                $"Time: {now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m\n" +
                $"Iterations total: {totalIterationCount}";

            if (TrySendTelegram(config, message))
            {
                lastDailyStatusDate = now.Date;
                Logger.Log("Отправлен ежедневный статус в Telegram.");
            }
        }

        private static void TrySendErrorNotification(AppConfig config, Exception ex, string category)
        {
            if (totalIterationCount - lastErrorNotificationIteration < ErrorNotificationIntervalIterations)
            {
                return;
            }

            string message =
                "ERROR ALERT\n" +
                $"Category: {category}\n" +
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Iteration: {totalIterationCount}\n" +
                $"Message: {ex.Message}";

            if (TrySendTelegram(config, message))
            {
                lastErrorNotificationIteration = totalIterationCount;
                Logger.Log("Отправлено уведомление об ошибке в Telegram.");
            }
        }

        private static bool TrySendTelegram(AppConfig config, string message)
        {
            try
            {
                TelegramService.SendMessage(config.Telegram.BotToken, config.Telegram.ChatId, message);
                return true;
            }
            catch (Exception telegramEx)
            {
                Logger.Log($"Не удалось отправить Telegram-сообщение: {telegramEx.Message}");
                return false;
            }
        }
    }
}