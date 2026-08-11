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
        private static int credentialsReloadRequested = 0;
        private const int ErrorNotificationIntervalIterations = 30;
        private static readonly TimeSpan DailyStatusTime = new TimeSpan(10, 30, 0);

        static void Main(string[] args)
        {
            AppConfig config;
            var startTime = UserTime.Now;

            try
            {
                config = ConfigurationLoader.Load(AppConstants.ConfigFileName, validateRequiredSecrets: false);
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
            var adminWeb = new AdminWebServer(
                config,
                shiftRulesStore,
                () => CreateStatusSnapshot(startTime, config),
                credentialsUpdated: () => Interlocked.Exchange(ref credentialsReloadRequested, 1));
            try
            {
                adminWeb.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Admin Web failed to start: {ex.Message}");
                Console.WriteLine($"Admin Web failed to start: {ex.Message}");
            }

            while (!ConfigurationLoader.HasOperationalCredentials(config))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            InitializeBrowserWithRetry(browserSession, config);
            SendStartupNotification(config, startTime);

            while (true)
            {
                if (Interlocked.Exchange(ref credentialsReloadRequested, 0) == 1)
                {
                    try { browserSession.Quit(); } catch { }
                    InitializeBrowserWithRetry(browserSession, config);
                }

                try
                {
                    iterationCount++;
                    totalIterationCount++;
                    lastIterationAt = UserTime.Now;
                    shiftChecker.CheckForShifts();
                    lastSuccessAt = UserTime.Now;
                }
                catch (WebDriverException ex) when (
                    ex.Message.Contains("invalid session id", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("not connected to DevTools", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"WebDriver упал, перезапуск: {ex.Message}");
                    lastErrorAt = UserTime.Now;
                    lastErrorMessage = ex.Message;
                    TrySendErrorNotification(config, ex, "WebDriver crash");
                    try { browserSession.Quit(); } catch { }
                    InitializeBrowserWithRetry(browserSession, config);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Произошла ошибка: {ex.Message}");
                    lastErrorAt = UserTime.Now;
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
                    lastErrorAt = UserTime.Now;
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
            var now = UserTime.Now;
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
            string message =
                "✅ Бот успішно запущено!\n\n" +
                "Усі налаштування завантажено. Бот уже перевіряє доступні зміни та повідомить вас, коли знайде відповідний варіант.\n\n" +
                $"Початок роботи: {startTime:dd.MM.yyyy о HH:mm}";

            if (TrySendTelegram(config, message))
            {
                Logger.Log("Отправлено стартовое сообщение в Telegram.");
            }
        }

        private static void TrySendDailyStatus(AppConfig config, DateTime startTime)
        {
            var now = UserTime.Now;
            if (!ShouldSendDailyStatus(now, lastDailyStatusDate))
            {
                return;
            }

            var uptime = now - startTime;
            string message =
                "🟢 Бот працює нормально\n\n" +
                "Це щоденне підтвердження, що бот активний і продовжує шукати зміни.\n\n" +
                $"Працює без перерви: {uptime.Days} дн. {uptime.Hours} год. {uptime.Minutes} хв.\n" +
                $"Виконано перевірок: {totalIterationCount}\n" +
                $"Станом на: {now:dd.MM.yyyy HH:mm}";

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
                "⚠️ Боту не вдалося виконати перевірку\n\n" +
                "Бот автоматично спробує продовжити роботу. Якщо такі повідомлення повторюються, відкрийте панель і перевірте налаштування GymBeam.\n\n" +
                $"Час: {UserTime.Now:dd.MM.yyyy HH:mm}";

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
