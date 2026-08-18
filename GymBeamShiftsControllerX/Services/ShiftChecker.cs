using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using GymBeamShiftsControllerX.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;

namespace GymBeamShiftsControllerX.Services
{
    public class ShiftChecker
    {
        private readonly BrowserSession _browserSession;
        private readonly AppConfig _config;
        private readonly ShiftRulesStore _shiftRulesStore;

        public ShiftChecker(BrowserSession browserSession, AppConfig config, ShiftRulesStore shiftRulesStore)
        {
            _browserSession = browserSession;
            _config = config;
            _shiftRulesStore = shiftRulesStore;
        }

        public void CheckForShifts()
        {
            while (true)
            {
                var driver = _browserSession.Driver;
                var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(60));

                driver.Navigate().Refresh();
                Logger.Log("Страница обновлена.");

                SelectAllShiftsPerPage(wait);

                var sortHeader = wait.Until(
                    ExpectedConditions.ElementToBeClickable(By.CssSelector(AppConstants.SortHeaderSelector))
                );
                sortHeader.Click();
                Logger.Log("Нажат заголовок 'Od' - первый клик");

                wait.Until(webDriver =>
                {
                    var tableRows = webDriver.FindElements(By.CssSelector(AppConstants.TableRowsSelector));
                    return tableRows.Count > 0;
                });
                Logger.Log("Таблица смен обновлена и содержит записи.");

                try
                {
                    Logger.Log("Ожидаем кнопку 'Only allow essential cookies' для закрытия cookie-баннера.");
                    var allowEssentialCookiesButton = wait.Until(
                        ExpectedConditions.ElementToBeClickable(By.Id(AppConstants.CookiesEssentialButtonId))
                    );
                    Logger.Log("Нажимаем кнопку 'Only allow essential cookies'.");
                    allowEssentialCookiesButton.Click();
                    Logger.Log("Кнопка 'Only allow essential cookies' нажата.");
                }
                catch (WebDriverTimeoutException)
                {
                    Logger.Log("Cookie-баннер не найден (timeout). Пропускаем...");
                }
                catch (NoSuchElementException)
                {
                    Logger.Log("Cookie-баннер не найден. Пропускаем...");
                }

                var rows = driver.FindElements(By.CssSelector(AppConstants.TableRowsSelector));
                Logger.Log($"Найдено строк: {rows.Count}");

                var shiftList = new List<ShiftEntry>();

                foreach (var row in rows)
                {
                    var cells = row.FindElements(By.TagName("td"));
                    if (cells.Count < 5)
                    {
                        continue;
                    }

                    string dateStr = cells[0].Text.Trim();
                    if (!DateTime.TryParseExact(
                            dateStr,
                            "dd.MM.yyyy",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsedDate))
                    {
                        continue;
                    }

                    string timeFrom = cells[1].Text.Trim();
                    string timeTo = cells[2].Text.Trim();
                    string userId = cells[3].Text.Trim();

                    IWebElement buttonElement = null;
                    try
                    {
                        buttonElement = cells[4].FindElement(By.CssSelector(AppConstants.SubscribeButtonSelector));
                    }
                    catch (NoSuchElementException)
                    {
                    }

                    shiftList.Add(new ShiftEntry
                    {
                        Date = parsedDate,
                        TimeFrom = timeFrom,
                        TimeTo = timeTo,
                        UserId = userId,
                        ButtonElement = buttonElement
                    });
                }

                var rules = _shiftRulesStore.GetSnapshot();
                var holidays = ParseDateSet(rules.Holidays);
                var excludedDates = ParseDateSet(rules.ExcludedDates);
                var startTimesToSkip = rules.StartTimesToSkip ?? new List<string>();
                var startTimesToSkipOnWeekendsAndHolidays =
                    rules.StartTimesToSkipOnWeekendsAndHolidays ?? new List<string>();
                var includedWeekdays = ParseWeekdaySet(rules.IncludedWeekdays);
                var favoriteShiftUserPriorities = ParseFavoriteShiftUserPriorities(rules.FavoriteShiftUsers);
                bool shiftRegistered = false;

                foreach (var shift in PrioritizeShiftsByFavoriteUsers(shiftList, favoriteShiftUserPriorities))
                {
                    if (!IsRelevantShift(
                            shift,
                            holidays,
                            excludedDates,
                            startTimesToSkip,
                            startTimesToSkipOnWeekendsAndHolidays,
                            includedWeekdays,
                            _config.Timing.ShiftMinHoursAhead,
                            _config.Timing.WeekendOrHolidayMinHoursAhead,
                            UserTime.Now))
                    {
                        continue;
                    }

                    {
                        bool isWeekendOrHoliday = IsWeekendOrHoliday(shift, holidays);
                        string message = $"Shift found: {shift.Date:dd.MM.yyyy} {shift.TimeFrom}-{shift.TimeTo}, User: {shift.UserId}";
                        Logger.Log($"Найдена релевантная смена: {message}");

                        Logger.Log("Нажимаем кнопку 'Prihlásiť'.");
                        shift.ButtonElement.Click();
                        Logger.Log("Кнопка 'Prihlásiť' нажата.");

                        bool subscriptionConfirmed = false;
                        try
                        {
                            var subscribeModal = wait.Until(
                                ExpectedConditions.ElementIsVisible(By.Id(AppConstants.SubscribeModalId))
                            );
                            Logger.Log("Модальное окно открыто.");

                            if (subscribeModal.Text.Contains(
                                    AppConstants.NewWorkersNoticeText,
                                    StringComparison.Ordinal))
                            {
                                Logger.Log("Смена предназначена для 'Noví brigádnici'. Пропускаем смену.");
                                CloseSubscribeModal(subscribeModal, wait);
                                continue;
                            }

                            string lunchRadioId = rules.TakeLunch
                                ? AppConstants.LunchYesRadioId
                                : AppConstants.LunchNoRadioId;
                            string lunchChoice = rules.TakeLunch ? "yes" : "no";
                            var lunchRadio = wait.Until(
                                ExpectedConditions.ElementToBeClickable(By.Id(lunchRadioId))
                            );
                            Logger.Log($"Нажимаем радиокнопку 'Lunch {lunchChoice}'.");
                            lunchRadio.Click();
                            Logger.Log($"Радиокнопка 'Lunch {lunchChoice}' нажата.");

                            var confirmButton = wait.Until(
                                ExpectedConditions.ElementToBeClickable(By.Id(AppConstants.SubscribeSubmitButtonId))
                            );
                            Logger.Log("Нажимаем кнопку 'Confirm'.");
                            confirmButton.Click();
                            Logger.Log("Кнопка 'Confirm' нажата.");

                            wait.Until(ExpectedConditions.InvisibilityOfElementLocated(By.Id(AppConstants.SubscribeModalId)));
                            Logger.Log("Модальное окно закрыто.");
                            SelectAllShiftsPerPage(wait);
                            subscriptionConfirmed = true;
                        }
                        catch (NoSuchElementException ex)
                        {
                            Logger.Log($"Ошибка: не удалось найти необходимые элементы в модальном окне. {ex.Message}");
                        }
                        catch (WebDriverTimeoutException ex)
                        {
                            Logger.Log($"Ошибка: модальное окно не открылось вовремя. {ex.Message}");
                        }

                        if (!subscriptionConfirmed)
                        {
                            Logger.Log("Регистрация смены не подтверждена. Уведомление не отправляется.");
                            driver.Navigate().Refresh();
                            Logger.Log("Страница обновлена.");
                            return;
                        }

                        SendShiftNotifications(message, isWeekendOrHoliday);
                        shiftRegistered = true;
                        break;
                    }
                }

                if (!shiftRegistered)
                {
                    return;
                }
            }
        }

        private static void SelectAllShiftsPerPage(WebDriverWait wait)
        {
            var selectElement = wait.Until(
                ExpectedConditions.ElementToBeClickable(By.Name(AppConstants.InvitationsTableLengthName))
            );
            var dropdown = new SelectElement(selectElement);
            dropdown.SelectByValue("100");
            Logger.Log("Выбрано значение 100 в выпадающем меню.");
        }

        private static void CloseSubscribeModal(IWebElement subscribeModal, WebDriverWait wait)
        {
            var dismissButtons = subscribeModal.FindElements(
                By.CssSelector(AppConstants.SubscribeModalDismissSelector)
            );

            if (dismissButtons.Count > 0)
            {
                dismissButtons[0].Click();
            }
            else
            {
                subscribeModal.SendKeys(Keys.Escape);
            }

            wait.Until(ExpectedConditions.InvisibilityOfElementLocated(By.Id(AppConstants.SubscribeModalId)));
            Logger.Log("Модальное окно пропущенной смены закрыто.");
        }

        private static bool TryParseShiftStart(ShiftEntry shift, out DateTime shiftStart)
        {
            shiftStart = default;
            var timeParts = shift.TimeFrom.Split(':');
            if (timeParts.Length != 2)
            {
                return false;
            }

            if (!int.TryParse(timeParts[0], out int shiftHour))
            {
                return false;
            }

            if (!int.TryParse(timeParts[1], out int shiftMinute))
            {
                return false;
            }

            shiftStart = shift.Date.AddHours(shiftHour).AddMinutes(shiftMinute);
            return true;
        }

        private static bool IsRelevantShift(
            ShiftEntry shift,
            HashSet<DateTime> holidays,
            HashSet<DateTime> excludedDates,
            IReadOnlyList<string> startTimesToSkip,
            IReadOnlyList<string> startTimesToSkipOnWeekendsAndHolidays,
            HashSet<DayOfWeek> includedWeekdays,
            int shiftMinHoursAhead,
            int weekendOrHolidayMinHoursAhead,
            DateTime now)
        {
            if (excludedDates.Contains(shift.Date.Date))
            {
                return false;
            }

            DayOfWeek dow = shift.Date.DayOfWeek;
            bool isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
            bool isIncludedWeekday = includedWeekdays.Contains(dow);
            bool isHoliday = holidays.Contains(shift.Date.Date);

            bool skipTimeApplies = (!isWeekend
                && !isHoliday)
                || startTimesToSkipOnWeekendsAndHolidays.Contains(shift.TimeFrom);
            if (skipTimeApplies && startTimesToSkip.Contains(shift.TimeFrom))
            {
                return false;
            }

            if (!TryParseShiftStart(shift, out DateTime shiftStart))
            {
                return false;
            }

            int minHoursAhead = isWeekend || isHoliday
                ? weekendOrHolidayMinHoursAhead
                : shiftMinHoursAhead;

            if (shiftStart < now.AddHours(minHoursAhead))
            {
                return false;
            }

            return (isWeekend || isHoliday || isIncludedWeekday) && shift.ButtonElement != null;
        }

        private static bool IsWeekendOrHoliday(ShiftEntry shift, HashSet<DateTime> holidays)
        {
            DayOfWeek dayOfWeek = shift.Date.DayOfWeek;
            return dayOfWeek == DayOfWeek.Saturday
                || dayOfWeek == DayOfWeek.Sunday
                || holidays.Contains(shift.Date.Date);
        }

        private void SendShiftNotifications(string message, bool isWeekendOrHoliday)
        {
            int notificationCount = isWeekendOrHoliday
                ? _config.Timing.ImportantShiftNotificationCount
                : 1;

            for (int notificationNumber = 1; notificationNumber <= notificationCount; notificationNumber++)
            {
                TelegramService.SendMessage(_config.Telegram.BotToken, _config.Telegram.ChatId, message);
                Logger.Log($"Сообщение {notificationNumber}/{notificationCount} отправлено в Telegram.");

                if (notificationNumber < notificationCount)
                {
                    Thread.Sleep(_config.Timing.ImportantShiftNotificationDelayMilliseconds);
                }
            }

            if (!isWeekendOrHoliday)
            {
                Thread.Sleep(_config.Timing.TelegramDelayMilliseconds);
            }
        }

        private static HashSet<DateTime> ParseDateSet(List<string> dates)
        {
            var result = new HashSet<DateTime>();

            if (dates == null)
            {
                return result;
            }

            foreach (var date in dates)
            {
                if (DateTime.TryParseExact(
                        date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedDate))
                {
                    result.Add(parsedDate.Date);
                }
            }

            return result;
        }

        private static HashSet<DayOfWeek> ParseWeekdaySet(List<string> weekdays)
        {
            var result = new HashSet<DayOfWeek>();
            if (weekdays == null)
            {
                return result;
            }

            foreach (var day in weekdays)
            {
                if (Enum.TryParse(day, true, out DayOfWeek parsedDay))
                {
                    result.Add(parsedDay);
                }
            }

            return result;
        }

        private static Dictionary<string, int> ParseFavoriteShiftUserPriorities(List<string> users)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (users == null)
            {
                return result;
            }

            foreach (var user in users)
            {
                if (string.IsNullOrWhiteSpace(user))
                {
                    continue;
                }

                var trimmed = user.Trim();
                if (!result.ContainsKey(trimmed))
                {
                    result.Add(trimmed, result.Count);
                }
            }

            return result;
        }

        private static List<ShiftEntry> PrioritizeShiftsByFavoriteUsers(
            List<ShiftEntry> shifts,
            Dictionary<string, int> favoriteShiftUserPriorities)
        {
            if (shifts == null || shifts.Count == 0 || favoriteShiftUserPriorities == null || favoriteShiftUserPriorities.Count == 0)
            {
                return shifts ?? new List<ShiftEntry>();
            }

            var result = new List<ShiftEntry>(shifts.Count);
            var processedDates = new HashSet<DateTime>();

            foreach (var currentShift in shifts)
            {
                var currentDate = currentShift.Date.Date;
                if (!processedDates.Add(currentDate))
                {
                    continue;
                }

                var sameDateShifts = new List<ShiftEntry>();
                foreach (var shift in shifts)
                {
                    if (shift.Date.Date == currentDate)
                    {
                        sameDateShifts.Add(shift);
                    }
                }

                result.AddRange(HasMultipleDifferentUsers(sameDateShifts)
                    ? PrioritizeSameDateShifts(sameDateShifts, favoriteShiftUserPriorities)
                    : sameDateShifts);
            }

            return result;
        }

        private static bool HasMultipleDifferentUsers(List<ShiftEntry> shifts)
        {
            var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shift in shifts)
            {
                users.Add((shift.UserId ?? string.Empty).Trim());
                if (users.Count >= 2)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<ShiftEntry> PrioritizeSameDateShifts(
            List<ShiftEntry> shifts,
            Dictionary<string, int> favoriteShiftUserPriorities)
        {
            var indexedShifts = new List<(ShiftEntry Shift, int Index)>();
            for (int i = 0; i < shifts.Count; i++)
            {
                indexedShifts.Add((shifts[i], i));
            }

            indexedShifts.Sort((left, right) =>
            {
                int priorityComparison = GetFavoriteShiftUserPriority(left.Shift, favoriteShiftUserPriorities)
                    .CompareTo(GetFavoriteShiftUserPriority(right.Shift, favoriteShiftUserPriorities));
                return priorityComparison != 0
                    ? priorityComparison
                    : left.Index.CompareTo(right.Index);
            });

            var result = new List<ShiftEntry>(indexedShifts.Count);
            foreach (var indexedShift in indexedShifts)
            {
                result.Add(indexedShift.Shift);
            }

            return result;
        }

        private static int GetFavoriteShiftUserPriority(
            ShiftEntry shift,
            Dictionary<string, int> favoriteShiftUserPriorities)
        {
            if (string.IsNullOrWhiteSpace(shift.UserId))
            {
                return int.MaxValue;
            }

            return favoriteShiftUserPriorities.TryGetValue(shift.UserId.Trim(), out int priority)
                ? priority
                : int.MaxValue;
        }
    }
}
