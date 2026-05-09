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

        public ShiftChecker(BrowserSession browserSession, AppConfig config)
        {
            _browserSession = browserSession;
            _config = config;
        }

        public void CheckForShifts()
        {
            var driver = _browserSession.Driver;
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));

            driver.Navigate().Refresh();
            Logger.Log("Страница обновлена.");

            var selectElement = wait.Until(
                ExpectedConditions.ElementToBeClickable(By.Name(AppConstants.InvitationsTableLengthName))
            );
            var dropdown = new SelectElement(selectElement);
            dropdown.SelectByValue("100");
            Logger.Log("Выбрано значение 100 в выпадающем меню.");

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

            var holidays = ParseDateSet(_config.ShiftRules.Holidays);
            var excludedDates = ParseDateSet(_config.ShiftRules.ExcludedDates);
            var startTimesToSkip = _config.ShiftRules.StartTimesToSkip ?? new List<string>();
            var includedWeekdays = ParseWeekdaySet(_config.ShiftRules.IncludedWeekdays);

            foreach (var shift in shiftList)
            {
                if (startTimesToSkip.Contains(shift.TimeFrom))
                {
                    continue;
                }

                if (excludedDates.Contains(shift.Date.Date))
                {
                    continue;
                }

                DayOfWeek dow = shift.Date.DayOfWeek;
                bool isWeekend = (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday);
                bool isIncludedWeekday = includedWeekdays.Contains(dow);

                bool isHoliday = holidays.Contains(shift.Date.Date);

                var timeParts = shift.TimeFrom.Split(':');
                if (timeParts.Length != 2)
                {
                    continue;
                }

                if (!int.TryParse(timeParts[0], out int shiftHour))
                {
                    continue;
                }

                if (!int.TryParse(timeParts[1], out int shiftMinute))
                {
                    continue;
                }

                var shiftStart = shift.Date.AddHours(shiftHour).AddMinutes(shiftMinute);

                if (shiftStart < DateTime.Now.AddHours(_config.Timing.ShiftMinHoursAhead))
                {
                    continue;
                }

                if (
                    //(shift.UserId == "Lukáš Fialek" || shift.UserId == "Andrea Pavlíková" || shift.UserId == "Marián Sipko"
                    //||
                    (isWeekend || isHoliday || isIncludedWeekday) &&
                    shift.ButtonElement != null
                )
                {
                    string message = $"Shift found: {shift.Date:dd.MM.yyyy} {shift.TimeFrom}-{shift.TimeTo}, User: {shift.UserId}";
                    Logger.Log($"Найдена релевантная смена: {message}");

                    Logger.Log("Нажимаем кнопку 'Prihlásiť'.");
                    shift.ButtonElement.Click();
                    Logger.Log("Кнопка 'Prihlásiť' нажата.");

                    try
                    {
                        wait.Until(ExpectedConditions.ElementIsVisible(By.Id(AppConstants.SubscribeModalId)));
                        Logger.Log("Модальное окно открыто.");

                        var lunchNoRadio = wait.Until(
                            ExpectedConditions.ElementToBeClickable(By.Id(AppConstants.LunchNoRadioId))
                        );
                        Logger.Log("Нажимаем радиокнопку 'Lunch no'.");
                        lunchNoRadio.Click();
                        Logger.Log("Радиокнопка 'Lunch no' нажата.");

                        var confirmButton = wait.Until(
                            ExpectedConditions.ElementToBeClickable(By.Id(AppConstants.SubscribeSubmitButtonId))
                        );
                        Logger.Log("Нажимаем кнопку 'Confirm'.");
                        confirmButton.Click();
                        Logger.Log("Кнопка 'Confirm' нажата.");

                        wait.Until(ExpectedConditions.InvisibilityOfElementLocated(By.Id(AppConstants.SubscribeModalId)));
                        Logger.Log("Модальное окно закрыто.");
                    }
                    catch (NoSuchElementException ex)
                    {
                        Logger.Log($"Ошибка: не удалось найти необходимые элементы в модальном окне. {ex.Message}");
                    }
                    catch (WebDriverTimeoutException ex)
                    {
                        Logger.Log($"Ошибка: модальное окно не открылось вовремя. {ex.Message}");
                    }

                    TelegramService.SendMessage(_config.Telegram.BotToken, _config.Telegram.ChatId, message);
                    Logger.Log("Сообщение отправлено в Telegram.");
                    Thread.Sleep(_config.Timing.TelegramDelayMilliseconds);

                    driver.Navigate().Refresh();
                    Logger.Log("Страница обновлена.");
                    CheckForShifts();
                    return;
                }
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