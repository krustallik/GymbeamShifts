using System;
using System.Collections.Generic;
using System.Text.Json;
using GymBeamShiftsControllerX.Config;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class ShiftRulesStoreTests
{
    [Fact]
    public void Constructor_WithNullRules_UsesDefaults()
    {
        var store = new ShiftRulesStore(null!);

        var snapshot = store.GetSnapshot();

        Assert.False(snapshot.TakeLunch);
        Assert.Contains("Monday", snapshot.IncludedWeekdays);
        Assert.Empty(snapshot.StartTimesToSkip);
        Assert.Empty(snapshot.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Empty(snapshot.Holidays);
        Assert.Empty(snapshot.ExcludedDates);
        Assert.Empty(snapshot.FavoriteShiftUsers);
        Assert.Empty(snapshot.TargetShiftDateTimes);
    }

    [Fact]
    public void GetSnapshot_ReturnsIsolatedCopy()
    {
        var store = new ShiftRulesStore(new ShiftRulesSettings
        {
            TakeLunch = true,
            IncludedWeekdays = new List<string> { "Monday" },
            StartTimesToSkip = new List<string> { "22:00" },
            StartTimesToSkipOnWeekendsAndHolidays = new List<string> { "22:00" },
            ExcludedDates = new List<string> { "2026-01-01" },
            FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" },
            TargetShiftDateTime = "2026-08-25T08:00"
        });

        var firstSnapshot = store.GetSnapshot();
        firstSnapshot.IncludedWeekdays.Add("Friday");
        firstSnapshot.StartTimesToSkipOnWeekendsAndHolidays.Clear();
        firstSnapshot.ExcludedDates.Clear();
        firstSnapshot.FavoriteShiftUsers.Clear();

        var secondSnapshot = store.GetSnapshot();

        Assert.True(secondSnapshot.TakeLunch);
        Assert.Equal(new[] { "Monday" }, secondSnapshot.IncludedWeekdays);
        Assert.Equal(new[] { "22:00" }, secondSnapshot.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Equal(new[] { "2026-01-01" }, secondSnapshot.ExcludedDates);
        Assert.Equal(new[] { "Andrea Pavlíková" }, secondSnapshot.FavoriteShiftUsers);
        Assert.Equal(new[] { "2026-08-25T08:00" }, secondSnapshot.TargetShiftDateTimes);
    }

    [Fact]
    public void Update_ReplacesCurrentRulesAndCleansValues()
    {
        var store = new ShiftRulesStore(new ShiftRulesSettings());

        store.Update(new ShiftRulesUpdateRequest
        {
            TakeLunch = true,
            IncludedWeekdays = new List<string> { "Monday", " monday ", "Friday", "" },
            StartTimesToSkip = new List<string> { "22:00", "22:00", " 21:45 " },
            StartTimesToSkipOnWeekendsAndHolidays = new List<string> { " 21:45 ", "20:00" },
            Holidays = new List<string> { "2026-05-01", "2026-05-01" },
            ExcludedDates = new List<string> { "2026-04-01", " " },
            FavoriteShiftUsers = new List<string> { "Andrea Pavlíková", " andrea pavlíková ", "Lukáš Fialek" },
            TargetShiftDateTimes = new List<string>
            {
                " 2026-08-25T08:00 ",
                "2026-08-25T08:00",
                "2026-08-26T09:30"
            }
        });

        var snapshot = store.GetSnapshot();
        Assert.True(snapshot.TakeLunch);
        Assert.Equal(new[] { "Monday", "Friday" }, snapshot.IncludedWeekdays);
        Assert.Equal(new[] { "22:00", "21:45" }, snapshot.StartTimesToSkip);
        Assert.Equal(new[] { "21:45" }, snapshot.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Equal(new[] { "2026-05-01" }, snapshot.Holidays);
        Assert.Equal(new[] { "2026-04-01" }, snapshot.ExcludedDates);
        Assert.Equal(new[] { "Andrea Pavlíková", "Lukáš Fialek" }, snapshot.FavoriteShiftUsers);
        Assert.Equal(
            new[] { "2026-08-25T08:00", "2026-08-26T09:30" },
            snapshot.TargetShiftDateTimes);
    }

    [Fact]
    public void Update_WithNullLists_StoresEmptyLists()
    {
        var store = new ShiftRulesStore(new ShiftRulesSettings());

        var snapshot = store.Update(new ShiftRulesUpdateRequest
        {
            IncludedWeekdays = null!,
            StartTimesToSkip = null!,
            StartTimesToSkipOnWeekendsAndHolidays = null!,
            Holidays = null!,
            ExcludedDates = null!,
            FavoriteShiftUsers = null!,
            TargetShiftDateTimes = null!
        });

        Assert.Empty(snapshot.IncludedWeekdays);
        Assert.Empty(snapshot.StartTimesToSkip);
        Assert.Empty(snapshot.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Empty(snapshot.Holidays);
        Assert.Empty(snapshot.ExcludedDates);
        Assert.Empty(snapshot.FavoriteShiftUsers);
        Assert.Empty(snapshot.TargetShiftDateTimes);
    }

    [Fact]
    public void SaveShiftRules_UpdatesOnlyShiftRulesAndKeepsSecretPlaceholders()
    {
        string fileName = $"appconfig.test.{Guid.NewGuid():N}.json";
        string path = System.IO.Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            string initialJson = """
{
  "Auth": {
    "LoginUrl": "https://part-time.gymbeam.com/web/login",
    "Login": "${GYMBEAM_AUTH_LOGIN}",
    "Password": "${GYMBEAM_AUTH_PASSWORD}",
    "SuccessUrlContains": "/news"
  },
  "Telegram": {
    "BotToken": "${GYMBEAM_TELEGRAM_BOT_TOKEN}",
    "ChatId": "${GYMBEAM_TELEGRAM_CHAT_ID}"
  },
  "Browser": {
    "Headless": true,
    "WindowSize": "1920,1080",
    "DisableGpu": true
  },
  "Timing": {
    "CheckIntervalMinutes": 2,
    "ShiftMinHoursAhead": 48,
    "DriverRestartAfterIterations": 100,
    "TelegramDelayMilliseconds": 1000
  },
  "ShiftRules": {
    "IncludedWeekdays": [ "Monday" ],
    "StartTimesToSkip": [ "22:00" ],
    "Holidays": [],
    "ExcludedDates": []
  }
}
""";
            System.IO.File.WriteAllText(path, initialJson);

            ConfigurationLoader.SaveShiftRules(fileName, new ShiftRulesSettings
            {
                TakeLunch = true,
                IncludedWeekdays = new List<string> { "Friday" },
                StartTimesToSkip = new List<string> { "21:45" },
                StartTimesToSkipOnWeekendsAndHolidays = new List<string> { "21:45" },
                Holidays = new List<string> { "2026-05-08" },
                ExcludedDates = new List<string> { "2026-05-09" },
                FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" },
                TargetShiftDateTimes = new List<string>
                {
                    "2026-08-25T08:00",
                    "2026-08-26T09:30"
                }
            });

            string updated = System.IO.File.ReadAllText(path);

            Assert.Contains("\"Login\": \"${GYMBEAM_AUTH_LOGIN}\"", updated);
            Assert.Contains("\"Password\": \"${GYMBEAM_AUTH_PASSWORD}\"", updated);
            Assert.Contains("\"BotToken\": \"${GYMBEAM_TELEGRAM_BOT_TOKEN}\"", updated);
            Assert.Contains("\"IncludedWeekdays\": [", updated);
            Assert.Contains("\"Friday\"", updated);
            Assert.Contains("\"21:45\"", updated);
            using var updatedJson = JsonDocument.Parse(updated);
            Assert.Equal(
                "21:45",
                updatedJson.RootElement.GetProperty("ShiftRules")
                    .GetProperty("StartTimesToSkipOnWeekendsAndHolidays")[0].GetString());
            Assert.Contains("\"2026-05-08\"", updated);
            Assert.Contains("\"2026-05-09\"", updated);
            Assert.Contains("\"FavoriteShiftUsers\": [", updated);
            Assert.True(updatedJson.RootElement.GetProperty("ShiftRules").GetProperty("TakeLunch").GetBoolean());
            Assert.Equal(
                "Andrea Pavlíková",
                updatedJson.RootElement.GetProperty("ShiftRules").GetProperty("FavoriteShiftUsers")[0].GetString());
            Assert.Equal(
                new[] { "2026-08-25T08:00", "2026-08-26T09:30" },
                updatedJson.RootElement.GetProperty("ShiftRules")
                    .GetProperty("TargetShiftDateTimes")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    [Fact]
    public void SaveShiftMinHoursAhead_UpdatesOnlyTimingFieldAndKeepsOtherSections()
    {
        string fileName = $"appconfig.test.{Guid.NewGuid():N}.json";
        string path = System.IO.Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            string initialJson = """
{
  "Auth": {
    "LoginUrl": "https://part-time.gymbeam.com/web/login",
    "Login": "${GYMBEAM_AUTH_LOGIN}",
    "Password": "${GYMBEAM_AUTH_PASSWORD}",
    "SuccessUrlContains": "/news"
  },
  "Telegram": {
    "BotToken": "${GYMBEAM_TELEGRAM_BOT_TOKEN}",
    "ChatId": "${GYMBEAM_TELEGRAM_CHAT_ID}"
  },
  "Timing": {
    "CheckIntervalMinutes": 2,
    "ShiftMinHoursAhead": 48,
    "DriverRestartAfterIterations": 100,
    "TelegramDelayMilliseconds": 1000
  },
  "ShiftRules": {
    "IncludedWeekdays": [ "Monday" ],
    "StartTimesToSkip": [ "22:00" ],
    "Holidays": [],
    "ExcludedDates": []
  }
}
""";
            System.IO.File.WriteAllText(path, initialJson);

            ConfigurationLoader.SaveShiftMinHoursAhead(fileName, 72);

            string updated = System.IO.File.ReadAllText(path);

            Assert.Contains("\"ShiftMinHoursAhead\": 72", updated);
            Assert.Contains("\"CheckIntervalMinutes\": 2", updated);
            Assert.Contains("\"IncludedWeekdays\": [", updated);
            Assert.Contains("\"Monday\"", updated);
            Assert.Contains("\"Login\": \"${GYMBEAM_AUTH_LOGIN}\"", updated);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    [Fact]
    public void SaveShiftTimingSettings_CreatesTimingSectionWhenMissing()
    {
        string fileName = $"appconfig.test.{Guid.NewGuid():N}.json";
        string path = System.IO.Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            System.IO.File.WriteAllText(path, """
            {
              "Auth": {},
              "Telegram": {},
              "Timing": null,
              "ShiftRules": {}
            }
            """);

            ConfigurationLoader.SaveShiftTimingSettings(fileName, 72, 24, 6, 45000);

            using var json = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var timing = json.RootElement.GetProperty("Timing");
            Assert.Equal(72, timing.GetProperty("ShiftMinHoursAhead").GetInt32());
            Assert.Equal(24, timing.GetProperty("WeekendOrHolidayMinHoursAhead").GetInt32());
            Assert.Equal(6, timing.GetProperty("ImportantShiftNotificationCount").GetInt32());
            Assert.Equal(45000, timing.GetProperty("ImportantShiftNotificationDelayMilliseconds").GetInt32());
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SaveShiftRules_WithNullRules_WritesDefaultRules()
    {
        string fileName = $"appconfig.test.{Guid.NewGuid():N}.json";
        string path = System.IO.Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            System.IO.File.WriteAllText(path, "{ \"ShiftRules\": {} }");

            ConfigurationLoader.SaveShiftRules(fileName, null!);

            using var json = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var rules = json.RootElement.GetProperty("ShiftRules");
            Assert.False(rules.GetProperty("TakeLunch").GetBoolean());
            Assert.Equal(2, rules.GetProperty("IncludedWeekdays").GetArrayLength());
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

}
