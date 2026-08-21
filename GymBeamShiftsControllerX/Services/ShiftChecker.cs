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
        private readonly Action<string> _sendTelegramMessage;
        private readonly HashSet<string> _skippedNewWorkerShiftIds = new(StringComparer.Ordinal);

        public ShiftChecker(
            BrowserSession browserSession,
            AppConfig config,
            ShiftRulesStore shiftRulesStore,
            Action<string>? sendTelegramMessage = null)
        {
            _browserSession = browserSession;
            _config = config;
            _shiftRulesStore = shiftRulesStore;
            _sendTelegramMessage = sendTelegramMessage
                ?? (message => TelegramService.SendMessage(
                    _config.Telegram.BotToken,
                    _config.Telegram.ChatId,
                    message));
        }

        public void CheckForShifts()
        {
            var notifiedUnavailableTargetStarts = new HashSet<DateTime>();

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

                var shiftList = CollectShiftsAcrossPages(driver, wait);

                var rules = _shiftRulesStore.GetSnapshot();
                var holidays = ParseDateSet(rules.Holidays);
                var excludedDates = ParseDateSet(rules.ExcludedDates);
                var startTimesToSkip = rules.StartTimesToSkip ?? new List<string>();
                var startTimesToSkipOnWeekendsAndHolidays =
                    rules.StartTimesToSkipOnWeekendsAndHolidays ?? new List<string>();
                var includedWeekdays = ParseWeekdaySet(rules.IncludedWeekdays);
                var favoriteShiftUserPriorities = ParseFavoriteShiftUserPriorities(rules.FavoriteShiftUsers);
                bool shiftRegistered = false;

                NotifyAboutUnavailableTargetShifts(
                    shiftList,
                    rules.TargetShiftDateTimes,
                    notifiedUnavailableTargetStarts);

                foreach (var shift in PrioritizeShiftsByFavoriteUsers(shiftList, favoriteShiftUserPriorities))
                {
                    if (_skippedNewWorkerShiftIds.Contains(shift.ShiftIdentifier))
                    {
                        continue;
                    }

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
                        string message = BuildSuccessfulShiftMessage(shift);
                        Logger.Log(
                            $"Найдена релевантная смена: {shift.Date:dd.MM.yyyy} "
                            + $"{shift.TimeFrom}-{shift.TimeTo}, User: {shift.UserId}");

                        if (!NavigateToPage(driver, wait, shift.PageNumber))
                        {
                            Logger.Log(
                                $"Не удалось перейти на страницу {shift.PageNumber} "
                                + $"для смены {shift.ShiftIdentifier}. Смена пропущена.");
                            continue;
                        }

                        var currentButton = FindCurrentShiftButton(driver, shift);
                        if (currentButton == null)
                        {
                            Logger.Log(
                                $"Не удалось повторно найти кнопку смены {shift.ShiftIdentifier} "
                                + $"на странице {shift.PageNumber}. Смена пропущена.");
                            continue;
                        }

                        Logger.Log("Нажимаем кнопку 'Prihlásiť'.");
                        ScrollIntoViewAndClick(driver, wait, currentButton);
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
                                _skippedNewWorkerShiftIds.Add(shift.ShiftIdentifier);
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

                            WaitForSubscribeModalToClose(wait);
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
            wait.Until(webDriver =>
            {
                try
                {
                    var currentSelect = new SelectElement(
                        webDriver.FindElement(By.Name(AppConstants.InvitationsTableLengthName))
                    );
                    return currentSelect.SelectedOption.GetAttribute("value") == "100";
                }
                catch (StaleElementReferenceException)
                {
                    return false;
                }
            });
            wait.Until(webDriver =>
            {
                foreach (var processingElement in webDriver.FindElements(By.CssSelector(".dataTables_processing")))
                {
                    try
                    {
                        if (processingElement.Displayed)
                        {
                            return false;
                        }
                    }
                    catch (StaleElementReferenceException)
                    {
                    }
                }

                return true;
            });
            Logger.Log("Выбрано значение 100 в выпадающем меню.");
        }

        private static List<ShiftEntry> CollectShiftsAcrossPages(
            IWebDriver driver,
            WebDriverWait wait)
        {
            const int maximumPages = 5;
            var shifts = new List<ShiftEntry>();

            NavigateToPage(driver, wait, 1);
            int pageNumber = GetActivePageNumber(driver) ?? 1;

            while (pageNumber <= maximumPages)
            {
                var pageShifts = ParseCurrentPage(driver, pageNumber);
                shifts.AddRange(pageShifts);
                Logger.Log(
                    $"На странице {pageNumber} найдено строк: "
                    + $"{driver.FindElements(By.CssSelector(AppConstants.TableRowsSelector)).Count}. "
                    + $"Распознано смен: {pageShifts.Count}.");

                if (pageNumber == maximumPages || !TryMoveToAdjacentPage(driver, wait, moveForward: true))
                {
                    break;
                }

                pageNumber = GetActivePageNumber(driver) ?? pageNumber + 1;
            }

            Logger.Log($"Собран общий список смен: {shifts.Count} (страниц обработано: {pageNumber}).");
            return shifts;
        }

        private static List<ShiftEntry> ParseCurrentPage(IWebDriver driver, int pageNumber)
        {
            var shifts = new List<ShiftEntry>();
            var rows = driver.FindElements(By.CssSelector(AppConstants.TableRowsSelector));

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

                IWebElement? buttonElement = null;
                try
                {
                    buttonElement = cells[4].FindElement(
                        By.CssSelector(AppConstants.SubscribeButtonSelector));
                }
                catch (NoSuchElementException)
                {
                }

                shifts.Add(new ShiftEntry
                {
                    Date = parsedDate,
                    TimeFrom = timeFrom,
                    TimeTo = timeTo,
                    UserId = userId,
                    ShiftIdentifier = GetShiftIdentifier(
                        buttonElement,
                        parsedDate,
                        timeFrom,
                        timeTo,
                        userId),
                    PageNumber = pageNumber,
                    ButtonElement = buttonElement
                });
            }

            return shifts;
        }

        private static IWebElement? FindCurrentShiftButton(IWebDriver driver, ShiftEntry shift)
        {
            foreach (var currentShift in ParseCurrentPage(driver, shift.PageNumber))
            {
                if (string.Equals(
                        currentShift.ShiftIdentifier,
                        shift.ShiftIdentifier,
                        StringComparison.Ordinal))
                {
                    return currentShift.ButtonElement;
                }
            }

            return null;
        }

        private static bool NavigateToPage(
            IWebDriver driver,
            WebDriverWait wait,
            int targetPageNumber)
        {
            int currentPageNumber = GetActivePageNumber(driver) ?? 1;
            int remainingMoves = 5;

            while (currentPageNumber != targetPageNumber && remainingMoves-- > 0)
            {
                bool moveForward = currentPageNumber < targetPageNumber;
                if (!TryMoveToAdjacentPage(driver, wait, moveForward))
                {
                    return false;
                }

                currentPageNumber = GetActivePageNumber(driver)
                    ?? currentPageNumber + (moveForward ? 1 : -1);
            }

            return currentPageNumber == targetPageNumber;
        }

        private static bool TryMoveToAdjacentPage(
            IWebDriver driver,
            WebDriverWait wait,
            bool moveForward)
        {
            string buttonId = moveForward
                ? AppConstants.NextPaginationButtonId
                : AppConstants.PreviousPaginationButtonId;
            var paginationButtons = driver.FindElements(By.Id(buttonId));
            if (paginationButtons.Count == 0)
            {
                return false;
            }

            var paginationButton = paginationButtons[0];
            string classes = paginationButton.GetAttribute("class") ?? string.Empty;
            string? ariaDisabled = paginationButton.GetAttribute("aria-disabled");
            var links = paginationButton.FindElements(By.TagName("a"));
            if (classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("disabled")
                || string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase)
                || links.Count == 0
                || string.Equals(
                    links[0].GetAttribute("aria-disabled"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int? previousPageNumber = GetActivePageNumber(driver);
            links[0].Click();

            wait.Until(webDriver =>
            {
                int? currentPageNumber = GetActivePageNumber(webDriver);
                return previousPageNumber.HasValue
                    ? currentPageNumber.HasValue && currentPageNumber != previousPageNumber
                    : IsTableProcessingComplete(webDriver);
            });
            wait.Until(IsTableProcessingComplete);
            return true;
        }

        private static int? GetActivePageNumber(IWebDriver driver)
        {
            var activePages = driver.FindElements(
                By.CssSelector(AppConstants.ActivePaginationPageSelector));
            return activePages.Count > 0 && int.TryParse(activePages[0].Text.Trim(), out int pageNumber)
                ? pageNumber
                : null;
        }

        private static bool IsTableProcessingComplete(IWebDriver driver)
        {
            foreach (var processingElement in driver.FindElements(By.CssSelector(".dataTables_processing")))
            {
                try
                {
                    if (processingElement.Displayed)
                    {
                        return false;
                    }
                }
                catch (StaleElementReferenceException)
                {
                }
            }

            return true;
        }

        private static string GetShiftIdentifier(
            IWebElement? buttonElement,
            DateTime date,
            string timeFrom,
            string timeTo,
            string userId)
        {
            string? dataId = buttonElement?.GetAttribute("data-id")?.Trim();
            return !string.IsNullOrWhiteSpace(dataId)
                ? dataId
                : $"{date:yyyy-MM-dd}|{timeFrom}|{timeTo}|{userId}";
        }

        private static void ScrollIntoViewAndClick(
            IWebDriver driver,
            WebDriverWait wait,
            IWebElement buttonElement)
        {
            wait.Until(_ =>
            {
                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "arguments[0].scrollIntoView({block: 'center', inline: 'nearest'});",
                    buttonElement);

                try
                {
                    if (!buttonElement.Displayed || !buttonElement.Enabled)
                    {
                        return false;
                    }

                    buttonElement.Click();
                    return true;
                }
                catch (ElementClickInterceptedException)
                {
                    return false;
                }
            });
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

            WaitForSubscribeModalToClose(wait);
            Logger.Log("Модальное окно пропущенной смены закрыто.");
        }

        private static void WaitForSubscribeModalToClose(WebDriverWait wait)
        {
            wait.Until(ExpectedConditions.InvisibilityOfElementLocated(By.Id(AppConstants.SubscribeModalId)));
            wait.Until(webDriver =>
            {
                foreach (var backdrop in webDriver.FindElements(By.CssSelector(".modal-backdrop")))
                {
                    try
                    {
                        if (backdrop.Displayed)
                        {
                            return false;
                        }
                    }
                    catch (StaleElementReferenceException)
                    {
                    }
                }

                return true;
            });
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

        private int NotifyAboutUnavailableTargetShifts(
            IReadOnlyList<ShiftEntry> shifts,
            IReadOnlyList<string> targetShiftDateTimes,
            HashSet<DateTime> notifiedTargetStarts)
        {
            var targetStarts = new HashSet<DateTime>();
            foreach (string value in targetShiftDateTimes ?? Array.Empty<string>())
            {
                if (DateTime.TryParseExact(
                        value,
                        "yyyy-MM-dd'T'HH:mm",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime targetStart))
                {
                    targetStarts.Add(targetStart);
                }
            }

            int notificationCount = 0;
            foreach (var shift in shifts)
            {
                if (shift.ButtonElement != null
                    || !TryParseShiftStart(shift, out DateTime shiftStart)
                    || !targetStarts.Contains(shiftStart)
                    || !notifiedTargetStarts.Add(shiftStart))
                {
                    continue;
                }

                _sendTelegramMessage(BuildUnavailableTargetShiftMessage(shift));
                Logger.Log(
                    $"Відправлено повідомлення про вибрану зміну без кнопки Prihlásiť: "
                    + $"{shift.Date:dd.MM.yyyy} {shift.TimeFrom}-{shift.TimeTo}.");
                notificationCount++;
            }

            return notificationCount;
        }

        private static string BuildUnavailableTargetShiftMessage(ShiftEntry shift)
        {
            return
                "⚠️ Знайдено вибрану зміну\n\n" +
                "Зміна, яку ви шукаєте, з’явилася:\n" +
                $"Дата: {shift.Date:dd.MM.yyyy}\n" +
                $"Час: {shift.TimeFrom}-{shift.TimeTo}\n" +
                $"Працівник: {shift.UserId}\n\n" +
                "Бот не може самостійно обрати цю зміну, оскільки для неї немає кнопки «Prihlásiť». " +
                "Перевірте зміну та спробуйте записатися вручну.";
        }

        private static string BuildSuccessfulShiftMessage(ShiftEntry shift)
        {
            return
                "✅ Зміну знайдено та успішно обрано\n\n" +
                $"Дата: {shift.Date:dd.MM.yyyy}\n" +
                $"Час: {shift.TimeFrom}-{shift.TimeTo}\n" +
                $"Працівник: {shift.UserId}";
        }

        private void SendShiftNotifications(string message, bool isWeekendOrHoliday)
        {
            int notificationCount = isWeekendOrHoliday
                ? _config.Timing.ImportantShiftNotificationCount
                : 1;

            for (int notificationNumber = 1; notificationNumber <= notificationCount; notificationNumber++)
            {
                _sendTelegramMessage(message);
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
