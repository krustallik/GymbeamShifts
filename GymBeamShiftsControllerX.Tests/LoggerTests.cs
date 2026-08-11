using System;
using System.IO;
using System.Threading.Tasks;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("MutableEnvironment")]
public class LoggerTests
{
    [Fact]
    public void Log_WritesTimestampedLineToConfiguredPath()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"gymbeam-log-{Guid.NewGuid():N}.txt");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", logPath);
        ResetLoggerPath();

        try
        {
            Logger.Log("test message");

            Assert.True(File.Exists(logPath));
            string content = File.ReadAllText(logPath);
            Assert.Contains(UserTime.Now.ToString("yyyy-MM-dd"), content);
            Assert.Contains("test message", content);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
        }
    }

    [Fact]
    public void GetLogFilePath_UsesDefaultWhenEnvMissing()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
        ResetLoggerPath();

        string path = Logger.GetLogFilePath();

        Assert.EndsWith("log.txt", path);
        Assert.Contains(AppContext.BaseDirectory, path);
    }

    [Fact]
    public void Log_DoesNotThrow_WhenDirectoryIsInvalid()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", "Z:\\definitely\\missing\\path\\log.txt");
        ResetLoggerPath();

        try
        {
            var exception = Record.Exception(() => Logger.Log("should not crash"));
            Assert.Null(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
        }
    }

    [Fact]
    public async Task Log_IsThreadSafe_ForParallelWrites()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"gymbeam-log-{Guid.NewGuid():N}.txt");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", logPath);
        ResetLoggerPath();

        try
        {
            Logger.Log("seed");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => Logger.Log($"line-{i}"))));

            var content = await File.ReadAllTextAsync(logPath);
            for (int i = 0; i < 20; i++)
            {
                Assert.Contains($"line-{i}", content);
            }
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
        }
    }

    [Fact]
    public void Log_RotatesWhenActiveFileReachesTenMegabytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gymbeam-log-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string logPath = Path.Combine(directory, "app.log");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", logPath);
        ResetLoggerPath();

        try
        {
            using (FileStream stream = File.Create(logPath)) stream.SetLength(10L * 1024 * 1024);

            Logger.Log("after rotation");

            Assert.Equal(10L * 1024 * 1024, new FileInfo(Path.Combine(directory, "app.1.log")).Length);
            Assert.Contains("after rotation", File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ResetLoggerPath()
    {
        ReflectionTestHelper.SetStaticField(typeof(Logger), "_logFilePath", null);
    }
}
