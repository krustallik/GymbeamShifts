using System;
using OpenQA.Selenium;

namespace GymBeamShiftsControllerX.Models
{
    public class ShiftEntry
    {
        public DateTime Date { get; set; }
        public string TimeFrom { get; set; } = null!;
        public string TimeTo { get; set; } = null!;
        public string UserId { get; set; } = null!;
        public string ShiftIdentifier { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public IWebElement? ButtonElement { get; set; }
    }
}
