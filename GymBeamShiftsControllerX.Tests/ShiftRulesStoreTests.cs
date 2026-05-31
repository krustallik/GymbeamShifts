using System;
using System.Collections.Generic;
using GymBeamShiftsControllerX.Config;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class ShiftRulesStoreTests
{
    [Fact]
    public void GetSnapshot_ReturnsIsolatedCopy()
    {
        var store = new ShiftRulesStore(new ShiftRulesSettings
        {
            IncludedWeekdays = new List<string> { "Monday" },
            ExcludedDates = new List<string> { "2026-01-01" }
        });

        var firstSnapshot = store.GetSnapshot();
        firstSnapshot.IncludedWeekdays.Add("Friday");
        firstSnapshot.ExcludedDates.Clear();

        var secondSnapshot = store.GetSnapshot();

        Assert.Equal(new[] { "Monday" }, secondSnapshot.IncludedWeekdays);
        Assert.Equal(new[] { "2026-01-01" }, secondSnapshot.ExcludedDates);
    }

    [Fact]
    public void Update_ReplacesCurrentRulesAndCleansValues()
    {
        var store = new ShiftRulesStore(new ShiftRulesSettings());

        store.Update(new ShiftRulesUpdateRequest
        {
            IncludedWeekdays = new List<string> { "Monday", " monday ", "Friday", "" },
            StartTimesToSkip = new List<string> { "22:00", "22:00", " 21:45 " },
            Holidays = new List<string> { "2026-05-01", "2026-05-01" },
            ExcludedDates = new List<string> { "2026-04-01", " " }
        });

        var snapshot = store.GetSnapshot();
        Assert.Equal(new[] { "Monday", "Friday" }, snapshot.IncludedWeekdays);
        Assert.Equal(new[] { "22:00", "21:45" }, snapshot.StartTimesToSkip);
        Assert.Equal(new[] { "2026-05-01" }, snapshot.Holidays);
        Assert.Equal(new[] { "2026-04-01" }, snapshot.ExcludedDates);
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
                IncludedWeekdays = new List<string> { "Friday" },
                StartTimesToSkip = new List<string> { "21:45" },
                Holidays = new List<string> { "2026-05-08" },
                ExcludedDates = new List<string> { "2026-05-09" }
            });

            string updated = System.IO.File.ReadAllText(path);

            Assert.Contains("\"Login\": \"${GYMBEAM_AUTH_LOGIN}\"", updated);
            Assert.Contains("\"Password\": \"${GYMBEAM_AUTH_PASSWORD}\"", updated);
            Assert.Contains("\"BotToken\": \"${GYMBEAM_TELEGRAM_BOT_TOKEN}\"", updated);
            Assert.Contains("\"IncludedWeekdays\": [", updated);
            Assert.Contains("\"Friday\"", updated);
            Assert.Contains("\"21:45\"", updated);
            Assert.Contains("\"2026-05-08\"", updated);
            Assert.Contains("\"2026-05-09\"", updated);
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

}
