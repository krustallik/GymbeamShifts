using System;
using System.IO;

namespace GymBeamShiftsControllerX.Services
{
    public static class Logger
    {
        private static readonly object LockObject = new object();
        private static readonly string LogFilePath = ResolveLogFilePath();

        public static string GetLogFilePath()
        {
            return LogFilePath;
        }

        public static void Log(string message)
        {
            lock (LockObject)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(
                        LogFilePath,
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