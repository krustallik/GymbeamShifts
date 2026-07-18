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
            var shiftRulesStore = new ShiftRulesStore(config.ShiftRules);
            var shiftChecker = new ShiftChecker(browserSession, config, shiftRulesStore);
            var adminWeb = new AdminWebServer(config, shiftRulesStore, () => CreateStatusSnapshot(startTime, config));
            try
            {
                adminWeb.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Admin Web failed to start: {ex.Message}");
                Console.WriteLine($"Admin Web failed to start: {ex.Message}");
            }

            InitializeBrowserWithRetry(browserSession, config);
            SendStartupNotification(config, startTime);

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
                    InitializeBrowserWithRetry(browserSession, config);
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

                    InitializeBrowserWithRetry(browserSession, config);
                    iterationCount = 0;
                }

                Thread.Sleep(config.Timing.CheckIntervalMinutes * 60 * 1000);
            }
        }

        private static void InitializeBrowserWithRetry(BrowserSession browserSession, AppConfig config)
        {
            while (true)
            {
                try
                {
                    browserSession.InitializeDriver();
                    return;
                }
                catch (Exception ex)
                {
                    lastErrorAt = DateTime.Now;
                    lastErrorMessage = ex.Message;
                    Logger.Log($"Не удалось инициализировать браузер. Повтор через 30 секунд: {ex.Message}");
                    TrySendErrorNotification(config, ex, "Browser initialization error");

                    try
                    {
                        browserSession.Quit();
                    }
                    catch
                    {
                    }

                    Thread.Sleep(TimeSpan.FromSeconds(30));
                }
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

        private static void SendStartupNotification(AppConfig config, DateTime startTime)
        {
            string host = Environment.GetEnvironmentVariable("HOSTNAME")
                ?? Environment.MachineName;
            bool inContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            string message =
                "STARTUP OK\n" +
                $"Time: {startTime:yyyy-MM-dd HH:mm:ss}\n" +
                $"Host: {host}\n" +
                $"Container: {inContainer}\n" +
                $"Admin port: {Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PORT") ?? "8080"}";

            if (TrySendTelegram(config, message))
            {
                Logger.Log("Отправлено стартовое сообщение в Telegram.");
            }
        }

        private static void TrySendDailyStatus(AppConfig config, DateTime startTime)
        {
            var now = DateTime.Now;
            if (!ShouldSendDailyStatus(now, lastDailyStatusDate))
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

        private static bool ShouldSendDailyStatus(DateTime now, DateTime lastSentDate)
        {
            return now.TimeOfDay >= DailyStatusTime && lastSentDate != now.Date;
        }

        private static void TrySendErrorNotification(AppConfig config, Exception ex, string category)
        {
            if (!ShouldSendErrorNotification(totalIterationCount, lastErrorNotificationIteration))
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

        private static bool ShouldSendErrorNotification(long totalIterations, long lastNotificationIteration)
        {
            return totalIterations - lastNotificationIteration >= ErrorNotificationIntervalIterations;
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