using System.Collections.Generic;

namespace GymBeamShiftsControllerX.Models
{
    public class AppConfig
    {
        public AuthSettings Auth { get; set; } = new AuthSettings();
        public TelegramSettings Telegram { get; set; } = new TelegramSettings();
        public BrowserSettings Browser { get; set; } = new BrowserSettings();
        public TimingSettings Timing { get; set; } = new TimingSettings();
        public ShiftRulesSettings ShiftRules { get; set; } = new ShiftRulesSettings();
    }

    public class AuthSettings
    {
        public string LoginUrl { get; set; } = string.Empty;
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string SuccessUrlContains { get; set; } = "/news";
    }

    public class TelegramSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
    }

    public class BrowserSettings
    {
        public bool Headless { get; set; } = true;
        public string WindowSize { get; set; } = "1920,1080";
        public bool DisableGpu { get; set; } = true;
    }

    public class TimingSettings
    {
        public int CheckIntervalMinutes { get; set; } = 2;
        public int ShiftMinHoursAhead { get; set; } = 48;
        public int WeekendOrHolidayMinHoursAhead { get; set; } = 28;
        public int DriverRestartAfterIterations { get; set; } = 100;
        public int TelegramDelayMilliseconds { get; set; } = 1000;
        public int ImportantShiftNotificationCount { get; set; } = 4;
        public int ImportantShiftNotificationDelayMilliseconds { get; set; } = 30000;
    }

    public class ShiftRulesSettings
    {
        public bool TakeLunch { get; set; } = false;
        public List<string> StartTimesToSkip { get; set; } = new List<string>();
        public List<string> StartTimesToSkipOnWeekendsAndHolidays { get; set; } = new List<string>();
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public List<string> IncludedWeekdays { get; set; } = new List<string> { "Monday", "Friday" };
        public List<string> FavoriteShiftUsers { get; set; } = new List<string>();
        public List<string> TargetShiftDateTimes { get; set; } = new List<string>();

        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetShiftDateTime { get; set; }
    }
}
