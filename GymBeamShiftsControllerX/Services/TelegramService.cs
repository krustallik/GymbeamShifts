using System.Collections.Generic;
using System.Net.Http;

namespace GymBeamShiftsControllerX.Services
{
    public static class TelegramService
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        public static void SendMessage(string botToken, string chatId, string message)
        {
            SendMessageAsync(botToken, chatId, message, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal static async Task SendMessageAsync(
            string botToken, string chatId, string message, CancellationToken cancellationToken)
        {
            string url = $"https://api.telegram.org/bot{botToken}/sendMessage";

            var data = new Dictionary<string, string>
            {
                { "chat_id", chatId },
                { "text", message }
            };

            using (var content = new FormUrlEncodedContent(data))
            {
                using var result = await HttpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
                EnsureSuccessfulResponse(result);
                Logger.Log($"Telegram message sent. Status: {result.StatusCode}");
            }
        }

        private static void EnsureSuccessfulResponse(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}
