using System;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Services
{
    public sealed class UserCredentialValidationResult
    {
        public bool TelegramValid { get; init; }
        public bool GymBeamValid { get; init; }
        public string TelegramMessage { get; init; } = string.Empty;
        public string GymBeamMessage { get; init; } = string.Empty;
        public bool Succeeded => TelegramValid && GymBeamValid;
    }

    public interface IUserCredentialValidator
    {
        UserCredentialValidationResult Validate(AppConfig current, UserCredentialsUpdateRequest credentials);
    }

    public sealed class UserCredentialValidator : IUserCredentialValidator
    {
        public UserCredentialValidationResult Validate(AppConfig current, UserCredentialsUpdateRequest credentials)
        {
            bool telegramValid;
            string telegramMessage;
            try
            {
                TelegramService.SendMessage(
                    credentials.TelegramBotToken,
                    credentials.TelegramChatId,
                    "✅ Telegram успішно підключено!\n\nБот може надсилати повідомлення в цей чат.");
                telegramValid = true;
                telegramMessage = "Тестове повідомлення доставлено.";
            }
            catch
            {
                telegramValid = false;
                telegramMessage = "Не вдалося надіслати тестове повідомлення. Перевірте токен і Chat ID.";
            }

            bool gymBeamValid;
            string gymBeamMessage;
            var candidate = new AppConfig
            {
                Auth = new AuthSettings
                {
                    LoginUrl = current.Auth.LoginUrl,
                    SuccessUrlContains = current.Auth.SuccessUrlContains,
                    Login = credentials.GymBeamLogin,
                    Password = credentials.GymBeamPassword
                },
                Telegram = current.Telegram,
                Browser = current.Browser,
                Timing = current.Timing,
                ShiftRules = current.ShiftRules
            };
            var session = new BrowserSession(candidate, remoteDebuggingPort: 0);
            try
            {
                session.InitializeDriver();
                gymBeamValid = true;
                gymBeamMessage = "Пробний вхід у GymBeam виконано успішно.";
            }
            catch
            {
                gymBeamValid = false;
                gymBeamMessage = "Не вдалося увійти в GymBeam. Перевірте логін і пароль.";
            }
            finally
            {
                try { session.Quit(); } catch { }
            }

            return new UserCredentialValidationResult
            {
                TelegramValid = telegramValid,
                GymBeamValid = gymBeamValid,
                TelegramMessage = telegramMessage,
                GymBeamMessage = gymBeamMessage
            };
        }
    }
}
