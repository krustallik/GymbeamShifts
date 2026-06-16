using System;
using System.IO;

namespace GymBeamShiftsControllerX.Services
{
    public static class Logger
    {
        private static readonly object LockObject = new object();
        private static string? _logFilePath;

        public static string GetLogFilePath()
        {
            return _logFilePath ??= ResolveLogFilePath();
        }

        public static void Log(string message)
        {
            lock (LockObject)
            {
                try
                {
                    string logFilePath = GetLogFilePath();
                    string? directory = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(
                        logFilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}"
                    );
                }
                catch
                {
                    // Logging must never crash the app.
                }
            }
        }

        private static string ResolveLogFilePath()
        {
            string envPath = Environment.GetEnvironmentVariable("GYMBEAM_LOG_PATH") ?? string.Empty;
            return string.IsNullOrWhiteSpace(envPath)
                ? Path.Combine(AppContext.BaseDirectory, AppConstants.LogFileName)
                : envPath;
        }
    }
}