using System.Collections.Generic;
using System.Net.Http;

namespace GymBeamShiftsControllerX.Services
{
    public static class TelegramService
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        public static void SendMessage(string botToken, string chatId, string message)
        {
            string url = $"https://api.telegram.org/bot{botToken}/sendMessage";

            var data = new Dictionary<string, string>
            {
                { "chat_id", chatId },
                { "text", message }
            };

            using (var content = new FormUrlEncodedContent(data))
            {
                using var result = HttpClient.PostAsync(url, content).Result;
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
