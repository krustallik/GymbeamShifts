namespace GymBeam.AdminManager.Docker;

internal static class BotIdValidator
{
    private const int MaximumLength = 64;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || !IsLowercaseLetter(value[0]))
        {
            return false;
        }

        return value.All(character => IsLowercaseLetter(character)
            || char.IsAsciiDigit(character)
            || character == '-');
    }

    private static bool IsLowercaseLetter(char character) => character is >= 'a' and <= 'z';
}
