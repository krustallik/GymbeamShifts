using System.Text;

namespace GymBeam.AdminManager.Credentials;

public static class CredentialKeys
{
    public const string GymBeamLogin = "GYMBEAM_AUTH_LOGIN";
    public const string GymBeamPassword = "GYMBEAM_AUTH_PASSWORD";
    public const string TelegramToken = "GYMBEAM_TELEGRAM_BOT_TOKEN";
    public const string TelegramChatId = "GYMBEAM_TELEGRAM_CHAT_ID";
    public const string BotAdminUser = "GYMBEAM_ADMIN_USER";
    public const string BotAdminPassword = "GYMBEAM_ADMIN_PASSWORD";
    public const string BotAdminTokenSecret = "GYMBEAM_ADMIN_TOKEN_SECRET";

    public static IReadOnlySet<string> Managed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        GymBeamLogin,
        GymBeamPassword,
        TelegramToken,
        TelegramChatId,
        BotAdminUser,
        BotAdminPassword,
        BotAdminTokenSecret
    };
}

public sealed class CredentialEnvDocument
{
    private readonly List<EnvLine> _lines;

    private CredentialEnvDocument(List<EnvLine> lines)
    {
        _lines = lines;
    }

    public static CredentialEnvDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string[] rawLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<EnvLine>(rawLines.Length);
        var managedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string originalLine in rawLines)
        {
            string rawLine = originalLine.EndsWith('\r') ? originalLine[..^1] : originalLine;
            string trimmed = rawLine.Trim();
            int separator = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || separator <= 0)
            {
                lines.Add(new EnvLine(rawLine, null, null));
                continue;
            }

            string key = trimmed[..separator].Trim();
            string encodedValue = trimmed[(separator + 1)..].Trim();
            if (CredentialKeys.Managed.Contains(key) && !managedKeys.Add(key))
            {
                throw new InvalidDataException($"Credential environment contains duplicate key '{key}'.");
            }

            lines.Add(new EnvLine(rawLine, key, encodedValue));
        }

        return new CredentialEnvDocument(lines);
    }

    public string? GetValue(string key)
    {
        EnvLine? line = _lines.SingleOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal));
        return line?.EncodedValue is null ? null : Decode(line.EncodedValue);
    }

    public CredentialEnvDocument Apply(IReadOnlyDictionary<string, string> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach ((string key, string value) in updates)
        {
            if (!CredentialKeys.Managed.Contains(key))
            {
                throw new ArgumentException("Unsupported credential key.", nameof(updates));
            }

            ValidateValue(value);
        }

        var remaining = new Dictionary<string, string>(updates, StringComparer.Ordinal);
        var updated = new List<EnvLine>(_lines.Count + remaining.Count);
        foreach (EnvLine line in _lines)
        {
            if (line.Key is not null && remaining.Remove(line.Key, out string? value))
            {
                string encoded = Encode(value);
                updated.Add(new EnvLine($"{line.Key}={encoded}", line.Key, encoded));
            }
            else
            {
                updated.Add(line);
            }
        }

        int insertionIndex = updated.Count > 0 && updated[^1].Raw.Length == 0
            ? updated.Count - 1
            : updated.Count;
        foreach ((string key, string value) in remaining)
        {
            string encoded = Encode(value);
            updated.Insert(insertionIndex++, new EnvLine($"{key}={encoded}", key, encoded));
        }

        return new CredentialEnvDocument(updated);
    }

    public string Serialize() => string.Join('\n', _lines.Select(line => line.Raw));

    private static string Encode(string value)
    {
        var encoded = new StringBuilder(value.Length + 2).Append('"');
        foreach (char character in value)
        {
            if (character is '\\' or '"')
            {
                encoded.Append('\\');
            }

            encoded.Append(character);
        }

        return encoded.Append('"').ToString();
    }

    private static string Decode(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var decoded = new StringBuilder(value.Length - 2);
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

    private static void ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Credential value is invalid.", nameof(value));
        }
    }

    private sealed record EnvLine(string Raw, string? Key, string? EncodedValue);
}
