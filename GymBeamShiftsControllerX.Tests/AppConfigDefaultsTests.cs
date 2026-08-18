using GymBeamShiftsControllerX.Models;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class AppConfigDefaultsTests
{
    [Fact]
    public void AppConfig_HasExpectedDefaultValues()
    {
        var cfg = new AppConfig();

        Assert.True(cfg.Browser.Headless);
        Assert.Equal("1920,1080", cfg.Browser.WindowSize);
        Assert.Equal(2, cfg.Timing.CheckIntervalMinutes);
        Assert.Equal(48, cfg.Timing.ShiftMinHoursAhead);
        Assert.Equal(28, cfg.Timing.WeekendOrHolidayMinHoursAhead);
        Assert.Equal(4, cfg.Timing.ImportantShiftNotificationCount);
        Assert.Equal(30000, cfg.Timing.ImportantShiftNotificationDelayMilliseconds);
        Assert.False(cfg.ShiftRules.TakeLunch);
        Assert.Empty(cfg.ShiftRules.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Contains("Monday", cfg.ShiftRules.IncludedWeekdays);
        Assert.Contains("Friday", cfg.ShiftRules.IncludedWeekdays);
        Assert.Empty(cfg.ShiftRules.FavoriteShiftUsers);
    }
}
