using System;
using OpenQA.Selenium;

namespace GymBeamShiftsControllerX.Models
{
    public class ShiftEntry
    {
        public DateTime Date { get; set; }
        public string TimeFrom { get; set; }
        public string TimeTo { get; set; }
        public string UserId { get; set; }
        public IWebElement ButtonElement { get; set; }
    }
}