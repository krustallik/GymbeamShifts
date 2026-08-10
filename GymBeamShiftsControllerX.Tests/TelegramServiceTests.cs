using System.Net;
using System.Net.Http;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public sealed class TelegramServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    public void EnsureSuccessfulResponse_AcceptsSuccessfulStatus(HttpStatusCode statusCode)
    {
        using var response = new HttpResponseMessage(statusCode);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(TelegramService), "EnsureSuccessfulResponse");

        var exception = Record.Exception(() => method.Invoke(null, new object[] { response }));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void EnsureSuccessfulResponse_RejectsTelegramErrorStatus(HttpStatusCode statusCode)
    {
        using var response = new HttpResponseMessage(statusCode);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(TelegramService), "EnsureSuccessfulResponse");

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, new object[] { response }));

        var httpException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(statusCode, httpException.StatusCode);
    }
}
