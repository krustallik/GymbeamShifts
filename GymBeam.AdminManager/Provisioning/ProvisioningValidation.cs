using System.Text.RegularExpressions;

namespace GymBeam.AdminManager.Provisioning;

public static partial class ProvisioningValidation
{
    private static readonly HashSet<string> ReservedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "manager", "caddy", "gymbeam-admin-manager"
    };

    public static ProvisioningSpec Validate(ProvisionBotRequest request, string baseDomain)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Identifier().IsMatch(request.BotId)
            || !Identifier().IsMatch(request.Subdomain)
            || ReservedIdentifiers.Contains(request.BotId)
            || ReservedIdentifiers.Contains(request.Subdomain)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > 100
            || ContainsControl(request.DisplayName)
            || !Domain().IsMatch(baseDomain)
            || !ValidSecret(request.GymBeamLogin)
            || !ValidSecret(request.GymBeamPassword)
            || !ValidSecret(request.TelegramToken)
            || !ValidSecret(request.TelegramChatId)
            || !ValidSecret(request.BotAdminUsername)
            || !ValidSecret(request.BotAdminPassword)
            || !ValidSecret(request.BotAdminToken))
        {
            throw new ArgumentException("Provisioning input is invalid.", nameof(request));
        }

        string id = request.BotId;
        return new ProvisioningSpec(
            id,
            request.DisplayName.Trim(),
            request.Subdomain,
            $"{request.Subdomain}.{baseDomain}",
            $"gymbeam-bot-{id}",
            $"gymbeam-shifts-{id}",
            id);
    }

    private static bool ValidSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 4096
        && !value.Contains('\0')
        && !value.Contains('\r')
        && !value.Contains('\n');

    private static bool ContainsControl(string value) => value.Any(char.IsControl);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}[a-z0-9]$|^[a-z]$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Domain();
}
