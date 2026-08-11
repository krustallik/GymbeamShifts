using System;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class UserTimeTests
{
    [Fact]
    public void FromUtc_UsesSummerOffsetForBratislava()
    {
        var utc = new DateTimeOffset(2026, 8, 11, 15, 16, 0, TimeSpan.Zero);

        DateTime local = UserTime.FromUtc(utc);

        Assert.Equal(new DateTime(2026, 8, 11, 17, 16, 0), local);
    }

    [Fact]
    public void FromUtc_UsesWinterOffsetForBratislava()
    {
        var utc = new DateTimeOffset(2026, 1, 11, 15, 16, 0, TimeSpan.Zero);

        DateTime local = UserTime.FromUtc(utc);

        Assert.Equal(new DateTime(2026, 1, 11, 16, 16, 0), local);
    }
}
