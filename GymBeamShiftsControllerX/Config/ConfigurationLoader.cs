using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Config
{
    public static class ConfigurationLoader
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static AppConfig Load(string fileName, bool validateRequiredSecrets = true)
        {
            LoadDotEnvIfExists();

            string configPath = FindConfigPath(fileName);
            string json = File.ReadAllText(configPath);
            AppConfig config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);

            if (config == null)
            {
                throw new InvalidOperationException("Файл конфигурации пустой или поврежден.");
            }

            ResolveSecretPlaceholders(config);
            ApplyEnvironmentOverrides(config);
            if (validateRequiredSecrets)
            {
                ValidateRequiredSecrets(config);
            }

            return config;
        }

        public static bool HasOperationalCredentials(AppConfig config)
        {
            return !string.IsNullOrWhiteSpace(config.Auth.Login)
                && !string.IsNullOrWhiteSpace(config.Auth.Password)
                && !string.IsNullOrWhiteSpace(config.Telegram.BotToken)
                && !string.IsNullOrWhiteSpace(config.Telegram.ChatId);
        }

        public static void SaveUserCredentials(
            string fileName,
            string gymBeamLogin,
            string gymBeamPassword,
            string telegramBotToken,
            string telegramChatId)
        {
            foreach (string value in new[] { gymBeamLogin, gymBeamPassword, telegramBotToken, telegramChatId })
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 4096
                    || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                {
                    throw new ArgumentException("Credential value is invalid.");
                }
            }

            string configuredPath = Environment.GetEnvironmentVariable("GYMBEAM_ENV_PATH") ?? string.Empty;
            string? envPath = string.IsNullOrWhiteSpace(configuredPath)
                ? FindOptionalFilePath(".env")
                : Path.GetFullPath(configuredPath);
            if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
            {
                throw new FileNotFoundException("Файл .env для credentials не знайдено.");
            }

            var updates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GYMBEAM_AUTH_LOGIN"] = gymBeamLogin,
                ["GYMBEAM_AUTH_PASSWORD"] = gymBeamPassword,
                ["GYMBEAM_TELEGRAM_BOT_TOKEN"] = telegramBotToken,
                ["GYMBEAM_TELEGRAM_CHAT_ID"] = telegramChatId
            };
            List<string> lines = File.ReadAllLines(envPath).ToList();
            var found = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < lines.Count; index++)
            {
                string trimmed = lines[index].Trim();
                int separator = trimmed.IndexOf('=');
                if (separator <= 0) continue;
                string key = trimmed[..separator].Trim();
                if (!updates.TryGetValue(key, out string? value)) continue;
                if (!found.Add(key))
                {
                    lines.RemoveAt(index--);
                    continue;
                }
                lines[index] = $"{key}={EncodeDotEnvValue(value)}";
            }
            foreach ((string key, string value) in updates)
            {
                if (!found.Contains(key)) lines.Add($"{key}={EncodeDotEnvValue(value)}");
            }
            File.WriteAllLines(envPath, lines);
        }

        private static string EncodeDotEnvValue(string value) =>
            $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        public static void Save(string fileName, AppConfig config)
        {
            string configPath = FindConfigPath(fileName);
            string json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(configPath, json);
        }

        public static void SaveShiftRules(string fileName, ShiftRulesSettings shiftRules)
        {
            string configPath = FindConfigPath(fileName);
            string json = File.ReadAllText(configPath);
            AppConfig fileConfig = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Файл конфигурации пустой или поврежден.");

            fileConfig.ShiftRules = shiftRules ?? new ShiftRulesSettings();
            string updatedJson = JsonSerializer.Serialize(fileConfig, SerializerOptions);
            File.WriteAllText(configPath, updatedJson);
        }

        public static void SaveShiftMinHoursAhead(string fileName, int shiftMinHoursAhead)
        {
            string configPath = FindConfigPath(fileName);
            string json = File.ReadAllText(configPath);
            AppConfig fileConfig = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Файл конфигурации пустой или поврежден.");

            fileConfig.Timing ??= new TimingSettings();
            fileConfig.Timing.ShiftMinHoursAhead = shiftMinHoursAhead;
            string updatedJson = JsonSerializer.Serialize(fileConfig, SerializerOptions);
            File.WriteAllText(configPath, updatedJson);
        }

        public static void SaveShiftTimingSettings(
            string fileName,
            int shiftMinHoursAhead,
            int weekendOrHolidayMinHoursAhead,
            int importantShiftNotificationCount,
            int importantShiftNotificationDelayMilliseconds)
        {
            string configPath = FindConfigPath(fileName);
            string json = File.ReadAllText(configPath);
            AppConfig fileConfig = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Файл конфигурации пустой или поврежден.");

            fileConfig.Timing ??= new TimingSettings();
            fileConfig.Timing.ShiftMinHoursAhead = shiftMinHoursAhead;
            fileConfig.Timing.WeekendOrHolidayMinHoursAhead = weekendOrHolidayMinHoursAhead;
            fileConfig.Timing.ImportantShiftNotificationCount = importantShiftNotificationCount;
            fileConfig.Timing.ImportantShiftNotificationDelayMilliseconds = importantShiftNotificationDelayMilliseconds;

            string updatedJson = JsonSerializer.Serialize(fileConfig, SerializerOptions);
            File.WriteAllText(configPath, updatedJson);
        }

        private static void LoadDotEnvIfExists()
        {
            string configuredPath = Environment.GetEnvironmentVariable("GYMBEAM_ENV_PATH") ?? string.Empty;
            string? envPath = string.IsNullOrWhiteSpace(configuredPath)
                ? FindOptionalFilePath(".env")
                : Path.GetFullPath(configuredPath);
            if (!string.IsNullOrWhiteSpace(configuredPath) && !File.Exists(envPath))
            {
                throw new FileNotFoundException("Configured credential environment file was not found.");
            }

            if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
            {
                return;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                {
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line[..separatorIndex].Trim();
                string value = DecodeDotEnvValue(line[(separatorIndex + 1)..].Trim());

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        private static string DecodeDotEnvValue(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                return value;
            }

            var decoded = new System.Text.StringBuilder(value.Length - 2);
            for (int index = 1; index < value.Length - 1; index++)
            {
                char character = value[index];
                if (character == '\\' && index + 1 < value.Length - 1
                    && value[index + 1] is '\\' or '"')
                {
                    character = value[++index];
                }

                decoded.Append(character);
            }

            return decoded.ToString();
        }

        private static void ResolveSecretPlaceholders(AppConfig config)
        {
            config.Auth.Login = ResolveValue(config.Auth.Login);
            config.Auth.Password = ResolveValue(config.Auth.Password);
            config.Telegram.BotToken = ResolveValue(config.Telegram.BotToken);
            config.Telegram.ChatId = ResolveValue(config.Telegram.ChatId);
        }

        private static string ResolveValue(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.StartsWith("${") && trimmed.EndsWith("}") && trimmed.Length > 3)
            {
                string envKey = trimmed[2..^1].Trim();
                return Environment.GetEnvironmentVariable(envKey) ?? string.Empty;
            }

            return value ?? string.Empty;
        }

        private static void ApplyEnvironmentOverrides(AppConfig config)
        {
            config.Auth.Login = GetEnvOrDefault("GYMBEAM_AUTH_LOGIN", config.Auth.Login);
            config.Auth.Password = GetEnvOrDefault("GYMBEAM_AUTH_PASSWORD", config.Auth.Password);
            config.Telegram.BotToken = GetEnvOrDefault("GYMBEAM_TELEGRAM_BOT_TOKEN", config.Telegram.BotToken);
            config.Telegram.ChatId = GetEnvOrDefault("GYMBEAM_TELEGRAM_CHAT_ID", config.Telegram.ChatId);
        }

        private static void ValidateRequiredSecrets(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Auth.Login))
            {
                throw new InvalidOperationException("Не задан логин. Укажите его в .env или appconfig.json.");
            }

            if (string.IsNullOrWhiteSpace(config.Auth.Password))
            {
                throw new InvalidOperationException("Не задан пароль. Укажите его в .env или appconfig.json.");
            }

            if (string.IsNullOrWhiteSpace(config.Telegram.BotToken))
            {
                throw new InvalidOperationException("Не задан Telegram BotToken. Укажите его в .env или appconfig.json.");
            }

            if (string.IsNullOrWhiteSpace(config.Telegram.ChatId))
            {
                throw new InvalidOperationException("Не задан Telegram ChatId. Укажите его в .env или appconfig.json.");
            }
        }

        private static string GetEnvOrDefault(string key, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(key) ?? string.Empty;
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string FindConfigPath(string fileName)
        {
            DirectoryInfo currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

            while (currentDirectory != null)
            {
                string candidate = Path.Combine(currentDirectory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                currentDirectory = currentDirectory.Parent;
            }

            throw new FileNotFoundException($"Не найден файл конфигурации '{fileName}'.");
        }

        private static string? FindOptionalFilePath(string fileName)
        {
            DirectoryInfo? currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

            while (currentDirectory != null)
            {
                string candidate = Path.Combine(currentDirectory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                currentDirectory = currentDirectory.Parent;
            }

            return null;
        }
    }
}
