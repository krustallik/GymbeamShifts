using System.Globalization;
using System.Reflection;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("Selenium")]
public sealed class ShiftCheckerWorkflowTests : IDisposable
{
    private readonly string _temporaryDirectory;
    private readonly ChromeDriver _driver;

    public ShiftCheckerWorkflowTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gymbeam-shift-checker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);

        var options = new ChromeOptions();
        options.AddArgument("--headless=new");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1280,900");

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        _driver = new ChromeDriver(service, options);
    }

    [Theory]
    [InlineData(true, "lunch_yes")]
    [InlineData(false, "lunch_no")]
    public void CheckForShifts_ClicksConfiguredLunchRadioAndConfirms(
        bool takeLunch,
        string expectedRadioId)
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows = CreateShiftRow(shiftDate, "23:00", "Test User");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(takeLunch);

        checker.CheckForShifts();

        Assert.Equal(
            $"#completed-{expectedRadioId}-Test%20User",
            new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_ClicksFavoriteUserFirstForSameDate()
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows =
            CreateShiftRow(shiftDate, "22:00", "Other User") +
            CreateShiftRow(shiftDate, "23:00", "Favorite User");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            favoriteUsers: new List<string> { "Favorite User" });

        checker.CheckForShifts();

        Assert.Equal(
            "#completed-lunch_no-Favorite%20User",
            new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_SkipsNewWorkersShiftAndRegistersNextEligibleShift()
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows =
            CreateShiftRow(shiftDate, "22:00", "Noví brigádnici - ranná") +
            CreateShiftRow(shiftDate, "23:00", "Eligible User");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(takeLunch: false);

        checker.CheckForShifts();

        Assert.Equal(
            "#completed-lunch_no-Eligible%20User",
            new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_RemembersSkippedNewWorkersShiftAcrossScans()
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows = CreateShiftRow(
            shiftDate,
            "22:00",
            "Noví brigádnici - ranná",
            "20777755");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(takeLunch: false);

        checker.CheckForShifts();
        checker.CheckForShifts();

        Assert.Equal(
            "1",
            _driver.ExecuteScript("return sessionStorage.getItem('subscribeClicks');"));
    }

    [Fact]
    public void CheckForShifts_SkipsMalformedExcludedAndUnavailableRows()
    {
        DateTime saturday = GetFutureSaturday();
        DateTime plainWeekday = GetFuturePlainWeekday();
        string saturdayText = saturday.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        string rows = $$"""
            <tr><td>too few cells</td></tr>
            <tr><td>not-a-date</td><td>23:00</td><td>23:30</td><td>Bad date</td><td><button class='subscribe_shift'>Join</button></td></tr>
            {{CreateShiftRow(saturday, "21:45", "Skipped time")}}
            {{CreateShiftRow(saturday, "22:00", "Excluded date")}}
            {{CreateShiftRow(plainWeekday, "23:00", "Plain weekday")}}
            <tr><td>{{saturdayText}}</td><td>23:00</td><td>23:30</td><td>No button</td><td></td></tr>
            """;
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            startTimesToSkip: new List<string> { "21:45" },
            excludedDates: new List<string> { saturday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            includedWeekdays: new List<string>());

        checker.CheckForShifts();

        Assert.Equal(string.Empty, new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_UsesHolidayRulesForWeekdayShift()
    {
        DateTime weekday = GetFuturePlainWeekday();
        string rows = CreateShiftRow(weekday, "23:00", "Holiday User");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: true,
            holidays: new List<string> { weekday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            includedWeekdays: new List<string>());

        checker.CheckForShifts();

        Assert.Equal(
            "#completed-lunch_yes-Holiday%20User",
            new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_SkipsFirstIrrelevantShiftAndRegistersNextEligibleShift()
    {
        DateTime firstSaturday = GetFutureSaturday();
        DateTime secondSaturday = firstSaturday.AddDays(7);
        string rows =
            CreateShiftRow(firstSaturday, "21:45", "Skipped User") +
            CreateShiftRow(secondSaturday, "23:00", "Eligible User");
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            excludedDates: new List<string>
            {
                firstSaturday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });

        checker.CheckForShifts();

        Assert.Equal(
            "#completed-lunch_no-Eligible%20User",
            new Uri(_driver.Url).Fragment);
    }

    [Fact]
    public void CheckForShifts_ProcessesSequentialShiftsWithoutRecursiveCall()
    {
        DateTime firstSaturday = GetFutureSaturday();
        DateTime secondSaturday = firstSaturday.AddDays(7);
        NavigateToScenario(CreateSequentialScenarioHtml(firstSaturday, secondSaturday));
        var checker = CreateChecker(takeLunch: true);

        checker.CheckForShifts();

        Assert.Equal(
            "#completed-lunch_yes-Second%20User",
            new Uri(_driver.Url).Fragment);
        Assert.Equal(
            "true",
            _driver.ExecuteScript("return sessionStorage.getItem('immediateReselect');"));
    }

    [Fact]
    public void CheckForShifts_NotifiesOncePerIterationWhenTargetShiftHasNoButton()
    {
        DateTime shiftDate = GetFutureSaturday();
        string dateText = shiftDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        string rows =
            $"<tr><td>{dateText}</td><td>08:00</td><td>16:00</td><td>Target User</td><td></td></tr>" +
            $"<tr><td>{dateText}</td><td>09:30</td><td>17:30</td><td>Second Target</td><td></td></tr>";
        string scenario = CreateScenarioHtml(rows);
        var messages = new List<string>();
        NavigateToScenario(scenario);
        var checker = CreateChecker(
            takeLunch: false,
            targetShiftDateTimes: new List<string>
            {
                $"{shiftDate:yyyy-MM-dd}T08:00",
                $"{shiftDate:yyyy-MM-dd}T09:30"
            },
            sendTelegramMessage: messages.Add);

        checker.CheckForShifts();
        NavigateToScenario(scenario);
        checker.CheckForShifts();

        Assert.Equal(4, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Contains("Знайдено вибрану зміну", message);
            Assert.Contains($"Дата: {shiftDate:dd.MM.yyyy}", message);
            Assert.Contains("немає кнопки «Prihlásiť»", message);
        });
        Assert.Equal(2, messages.Count(message => message.Contains("Час: 08:00-16:00")));
        Assert.Equal(2, messages.Count(message => message.Contains("Час: 09:30-17:30")));
    }

    [Fact]
    public void CheckForShifts_DoesNotSendUnavailableMessageWhenTargetShiftHasButton()
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows = CreateShiftRow(shiftDate, "08:00", "Target User");
        var messages = new List<string>();
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            targetShiftDateTimes: new List<string> { $"{shiftDate:yyyy-MM-dd}T08:00" },
            sendTelegramMessage: messages.Add);

        checker.CheckForShifts();

        Assert.Empty(messages);
    }

    [Fact]
    public void CheckForShifts_DoesNotNotifyWhenTargetDateTimeDoesNotMatch()
    {
        DateTime shiftDate = GetFutureSaturday();
        string dateText = shiftDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        string rows = $"<tr><td>{dateText}</td><td>08:00</td><td>16:00</td><td>Other shift</td><td></td></tr>";
        var messages = new List<string>();
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            targetShiftDateTimes: new List<string> { $"{shiftDate:yyyy-MM-dd}T09:00" },
            sendTelegramMessage: messages.Add);

        checker.CheckForShifts();

        Assert.Empty(messages);
    }

    [Fact]
    public void CheckForShifts_SendsUkrainianSuccessfulShiftMessage()
    {
        DateTime shiftDate = GetFutureSaturday();
        string rows = CreateShiftRow(shiftDate, "23:00", "Successful User");
        var messages = new List<string>();
        NavigateToScenario(CreateScenarioHtml(rows));
        var checker = CreateChecker(
            takeLunch: false,
            importantShiftNotificationCount: 1,
            sendTelegramMessage: messages.Add);

        checker.CheckForShifts();

        string message = Assert.Single(messages);
        Assert.Contains("Зміну знайдено та успішно обрано", message);
        Assert.Contains($"Дата: {shiftDate:dd.MM.yyyy}", message);
        Assert.Contains("Час: 23:00-23:30", message);
        Assert.Contains("Працівник: Successful User", message);
        Assert.DoesNotContain("Shift found:", message);
    }

    private ShiftChecker CreateChecker(
        bool takeLunch,
        List<string>? favoriteUsers = null,
        List<string>? startTimesToSkip = null,
        List<string>? excludedDates = null,
        List<string>? holidays = null,
        List<string>? includedWeekdays = null,
        List<string>? targetShiftDateTimes = null,
        int importantShiftNotificationCount = 0,
        Action<string>? sendTelegramMessage = null)
    {
        var config = new AppConfig
        {
            Telegram = new TelegramSettings { BotToken = "unused", ChatId = "unused" },
            Timing = new TimingSettings
            {
                ShiftMinHoursAhead = 1,
                WeekendOrHolidayMinHoursAhead = 1,
                ImportantShiftNotificationCount = importantShiftNotificationCount,
                ImportantShiftNotificationDelayMilliseconds = 0,
                TelegramDelayMilliseconds = 0
            },
            ShiftRules = new ShiftRulesSettings
            {
                TakeLunch = takeLunch,
                FavoriteShiftUsers = favoriteUsers ?? new List<string>(),
                StartTimesToSkip = startTimesToSkip ?? new List<string>(),
                ExcludedDates = excludedDates ?? new List<string>(),
                Holidays = holidays ?? new List<string>(),
                IncludedWeekdays = includedWeekdays ?? new List<string>(),
                TargetShiftDateTimes = targetShiftDateTimes ?? new List<string>()
            }
        };

        var browserSession = new BrowserSession(config);
        var driverField = typeof(BrowserSession).GetField(
            "<Driver>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BrowserSession.Driver backing field was not found.");
        driverField.SetValue(browserSession, _driver);

        return new ShiftChecker(
            browserSession,
            config,
            new ShiftRulesStore(config.ShiftRules),
            sendTelegramMessage);
    }

    private void NavigateToScenario(string html)
    {
        string path = Path.Combine(_temporaryDirectory, $"scenario-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, html);
        _driver.Navigate().GoToUrl(new Uri(path).AbsoluteUri);
    }

    private static string CreateScenarioHtml(string initialRows)
    {
        return $$"""
            <!doctype html>
            <html>
            <head><meta charset='utf-8'><title>Shift scenario</title></head>
            <body>
              <button id='cookies-consent-essential' onclick='this.remove()'>Accept cookies</button>
              <select name='invitations_table_length'><option value='100'>100</option></select>
              <table id='invitations_table'>
                <thead><tr><th>User</th><th>Od</th></tr></thead>
                <tbody id='shiftRows'>{{initialRows}}</tbody>
              </table>
              <div id='modal_subscribe' style='display:none'>
                <button class='btn-close' onclick='closeSubscription()'>Close</button>
                <h4 id='shiftTitle'></h4>
                <label><input id='lunch_yes' name='lunch' type='radio'> Yes</label>
                <label><input id='lunch_no' name='lunch' type='radio'> No</label>
                <button id='subscribe_submit' onclick='completeSubscription()'>Confirm</button>
              </div>
              <script>
                let selectedUser = '';
                function subscribe(user) {
                  const clickCount = Number(sessionStorage.getItem('subscribeClicks') || '0');
                  sessionStorage.setItem('subscribeClicks', String(clickCount + 1));
                  selectedUser = user;
                  document.getElementById('shiftTitle').textContent = user;
                  document.getElementById('modal_subscribe').style.display = 'block';
                }
                function closeSubscription() {
                  document.getElementById('modal_subscribe').style.display = 'none';
                }
                function completeSubscription() {
                  const selectedLunch = document.querySelector("input[name='lunch']:checked").id;
                  history.replaceState(null, '', '#completed-' + selectedLunch + '-' + encodeURIComponent(selectedUser));
                  document.getElementById('modal_subscribe').style.display = 'none';
                }
                if (location.hash.startsWith('#completed-')) {
                  document.getElementById('shiftRows').innerHTML = '<tr><td>completed</td></tr>';
                }
              </script>
            </body>
            </html>
            """;
    }

    private static string CreateSequentialScenarioHtml(DateTime firstDate, DateTime secondDate)
    {
        string firstRow = CreateShiftRow(firstDate, "22:00", "First User");
        string secondRow = CreateShiftRow(secondDate, "23:00", "Second User");
        return $$"""
            <!doctype html>
            <html>
            <head><meta charset='utf-8'><title>Sequential shifts</title></head>
            <body>
              <button id='cookies-consent-essential' onclick='this.remove()'>Accept cookies</button>
              <select name='invitations_table_length' onchange='trackPageSizeSelection(this)'>
                <option value='10'>10</option>
                <option value='100'>100</option>
              </select>
              <table id='invitations_table'>
                <thead><tr><th>User</th><th>Od</th></tr></thead>
                <tbody id='shiftRows'>{{firstRow}}</tbody>
              </table>
              <div id='modal_subscribe' style='display:none'>
                <input id='lunch_yes' name='lunch' type='radio'>
                <input id='lunch_no' name='lunch' type='radio'>
                <button id='subscribe_submit' onclick='completeSubscription()'>Confirm</button>
              </div>
              <script>
                let selectedUser = '';
                let confirmedInThisDocument = false;
                function trackPageSizeSelection(select) {
                  if (confirmedInThisDocument && select.value === '100') {
                    sessionStorage.setItem('immediateReselect', 'true');
                  }
                }
                function subscribe(user) {
                  selectedUser = user;
                  document.getElementById('modal_subscribe').style.display = 'block';
                }
                function completeSubscription() {
                  const lunch = document.querySelector("input[name='lunch']:checked").id;
                  const prefix = selectedUser === 'First User' ? '#step-1-' : '#completed-';
                  history.replaceState(null, '', prefix + lunch + '-' + encodeURIComponent(selectedUser));
                  document.querySelector("select[name='invitations_table_length']").value = '10';
                  confirmedInThisDocument = true;
                  document.getElementById('modal_subscribe').style.display = 'none';
                }
                if (location.hash.startsWith('#step-1-')) {
                  document.getElementById('shiftRows').innerHTML = `{{secondRow}}`;
                } else if (location.hash.startsWith('#completed-')) {
                  document.getElementById('shiftRows').innerHTML = '<tr><td>completed</td></tr>';
                }
              </script>
            </body>
            </html>
            """;
    }

    private static string CreateShiftRow(
        DateTime date,
        string timeFrom,
        string user,
        string? shiftIdentifier = null)
    {
        string dateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        shiftIdentifier ??= $"shift-{date:yyyyMMdd}-{timeFrom.Replace(':', '-')}-{Uri.EscapeDataString(user)}";
        return $$"""
            <tr>
              <td>{{dateText}}</td><td>{{timeFrom}}</td><td>23:30</td><td>{{user}}</td>
              <td><button class='subscribe_shift' data-id='{{shiftIdentifier}}' onclick="subscribe('{{user}}')">Join</button></td>
            </tr>
            """;
    }

    private static DateTime GetFutureSaturday()
    {
        DateTime candidate = UserTime.Now.Date.AddDays(2);
        while (candidate.DayOfWeek != DayOfWeek.Saturday)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    private static DateTime GetFuturePlainWeekday()
    {
        DateTime candidate = UserTime.Now.Date.AddDays(3);
        while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    public void Dispose()
    {
        try
        {
            _driver.Quit();
        }
        catch
        {
        }

        _driver.Dispose();
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

[CollectionDefinition("Selenium", DisableParallelization = true)]
public sealed class SeleniumCollection
{
}
