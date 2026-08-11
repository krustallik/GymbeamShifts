using System;

namespace GymBeamShiftsControllerX.Services
{
    public static class UserTime
    {
        private const string IanaTimeZoneId = "Europe/Bratislava";
        private const string WindowsTimeZoneId = "Central Europe Standard Time";

        public static TimeZoneInfo TimeZone { get; } = ResolveTimeZone();

        public static DateTime Now => FromUtc(DateTimeOffset.UtcNow);

        public static DateTime FromUtc(DateTimeOffset utcTime)
        {
            return TimeZoneInfo.ConvertTime(utcTime, TimeZone).DateTime;
        }

        private static TimeZoneInfo ResolveTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(IanaTimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById(WindowsTimeZoneId);
            }
        }
    }
}
