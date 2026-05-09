using System;
using System.IO;

namespace GymBeamShiftsControllerX.Services
{
    public static class Logger
    {
        private static readonly object LockObject = new object();
        private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, AppConstants.LogFileName);

        public static string GetLogFilePath()
        {
            return LogFilePath;
        }

        public static void Log(string message)
        {
            lock (LockObject)
            {
                File.AppendAllText(
                    LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}"
                );
            }
        }
    }
}