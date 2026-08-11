using System;
using System.IO;
using System.Text;

namespace GymBeamShiftsControllerX.Services
{
    public static class Logger
    {
        private const long MaximumLogBytes = 10L * 1024 * 1024;
        private const int MaximumLogFiles = 10;
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

                    string line = $"{UserTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
                    RotateIfNeeded(logFilePath, Encoding.UTF8.GetByteCount(line));
                    File.AppendAllText(logFilePath, line);
                }
                catch
                {
                    // Logging must never crash the app.
                }
            }
        }

        private static void RotateIfNeeded(string logFilePath, int incomingBytes)
        {
            if (!File.Exists(logFilePath)
                || new FileInfo(logFilePath).Length + incomingBytes <= MaximumLogBytes)
            {
                return;
            }

            string directory = Path.GetDirectoryName(logFilePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(logFilePath);
            string extension = Path.GetExtension(logFilePath);
            string ArchivePath(int index) => Path.Combine(directory, $"{baseName}.{index}{extension}");
            int maximumArchive = MaximumLogFiles - 1;

            string oldest = ArchivePath(maximumArchive);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (int index = maximumArchive - 1; index >= 1; index--)
            {
                string source = ArchivePath(index);
                if (File.Exists(source)) File.Move(source, ArchivePath(index + 1));
            }

            File.Move(logFilePath, ArchivePath(1));
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
