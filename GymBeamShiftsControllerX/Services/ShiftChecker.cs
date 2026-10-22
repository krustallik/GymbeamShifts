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
                Logger.Log("Нажат заголовок времени смены.");

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
                var startTimesToSkipOnWeekendsAndHolidays =
                    rules.StartTimesToSkipOnWeekendsAndHolidays ?? new List<string>();
                var includedWeekdays = ParseWeekdaySet(rules.IncludedWeekdays);
                shiftList = shiftList.Where(shift => ShiftPreferences.IsAllowed(shift, rules)).ToList();
                bool shiftRegistered = false;

                NotifyAboutUnavailableTargetShifts(
                    shiftList,
                    rules.TargetShiftDateTimes,
                    notifiedUnavailableTargetStarts);

                foreach (var shift in ShiftPreferences.Prioritize(shiftList, rules))
                {
                    if (_skippedNewWorkerShiftIds.Contains(shift.ShiftIdentifier))
                    {
                        continue;
                    }

                    if (!IsRelevantShift(
                            shift,
                            holidays,
                            excludedDates,
                            WeekdayTimeRules.Resolve(rules, shift.Date, holidays.Contains(shift.Date.Date)),
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
                            + $"{shift.TimeFrom}-{shift.TimeTo}, Місце: {shift.Location}, Тип: {shift.ShiftType}");

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
                            var subscribeModal = WaitForPortalModal(driver, wait,
                                AppConstants.SubscribeModalId, currentButton.GetAttribute("data-id"));
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

        private static IWebElement WaitForPortalModal(
            IWebDriver driver, WebDriverWait wait, string modalId, string? recordId)
        {
            var modal = wait.Until(ExpectedConditions.ElementIsVisible(By.Id(modalId)));

            var record = modal.FindElements(By.CssSelector("input[name='record_id']")).FirstOrDefault();
            if (record != null && !string.IsNullOrWhiteSpace(recordId)
                && record.GetAttribute("value") != recordId)
            {
                throw new InvalidOperationException("Portal modal contains a different shift record.");
            }
            return modal;
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

                if (!ShiftTimeParser.TryParseRange(cells[1].Text, out string timeFrom, out string timeTo))
                {
                    Logger.Log($"Пропущено рядок за {dateStr}: некоректний діапазон часу.");
                    continue;
                }

                string location = ShiftPreferences.Normalize(cells[2].Text);
                string shiftType = ShiftPreferences.Normalize(cells[3].Text);

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
                    Location = location,
                    ShiftType = shiftType,
                    ShiftIdentifier = GetShiftIdentifier(
                        buttonElement,
                        parsedDate,
                        timeFrom,
                        timeTo,
                        location,
                        shiftType),
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

            bool PageChanged(IWebDriver webDriver)
            {
                int? currentPageNumber = GetActivePageNumber(webDriver);
                return previousPageNumber.HasValue
                    ? currentPageNumber.HasValue && currentPageNumber != previousPageNumber
                    : IsTableProcessingComplete(webDriver);
            }

            wait.Until(PageChanged);
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
            string location,
            string shiftType)
        {
            string? dataId = buttonElement?.GetAttribute("data-id")?.Trim();
            return !string.IsNullOrWhiteSpace(dataId)
                ? dataId
                : $"{date:yyyy-MM-dd}|{timeFrom}|{timeTo}|{location}|{shiftType}";
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
            if (!TimeOnly.TryParseExact(shift.TimeFrom, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start))
            {
                return false;
            }

            shiftStart = shift.Date.Date.Add(start.ToTimeSpan());
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

            var applicableSkipTimes = isWeekend || isHoliday
                ? startTimesToSkip.Where(time => startTimesToSkipOnWeekendsAndHolidays.Contains(time))
                : startTimesToSkip;
            if (ShiftTimeParser.IsBlockedStart(shift.TimeFrom, applicableSkipTimes))
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
                $"Місце: {shift.Location}\n" +
                $"Тип зміни: {shift.ShiftType}\n\n" +
                "Бот не може самостійно обрати цю зміну, оскільки для неї немає кнопки «Prihlásiť». " +
                "Перевірте зміну та спробуйте записатися вручну.";
        }

        private static string BuildSuccessfulShiftMessage(ShiftEntry shift)
        {
            return
                "✅ Зміну знайдено та успішно обрано\n\n" +
                $"Дата: {shift.Date:dd.MM.yyyy}\n" +
                $"Час: {shift.TimeFrom}-{shift.TimeTo}\n" +
                $"Місце: {shift.Location}\n" +
                $"Тип зміни: {shift.ShiftType}";
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

    }
}
