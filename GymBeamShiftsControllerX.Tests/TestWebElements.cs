using OpenQA.Selenium;
using Moq;

namespace GymBeamShiftsControllerX.Tests;

internal static class TestWebElements
{
    public static IWebElement CreateButton() => new Mock<IWebElement>().Object;
}
