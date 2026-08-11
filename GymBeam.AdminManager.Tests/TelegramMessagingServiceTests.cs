using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Messaging;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public sealed class TelegramMessagingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"telegram-messaging-{Guid.NewGuid():N}");

    [Fact]
    public async Task SendAsync_UsesOnlySelectedBotsCredentials()
    {
        ManagedBot bot1 = CreateBot("bot1");
        ManagedBot bot2 = CreateBot("bot2");
        WriteCredentials(bot1, "token1", "chat1");
        WriteCredentials(bot2, "token2", "chat2");
        var sender = new RecordingSender();
        var service = CreateService([bot1, bot2], sender);

        TelegramDeliveryResult result = await service.SendAsync("bot2", "Hello", "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(("token2", "chat2", "Hello"), Assert.Single(sender.Messages));
    }

    [Fact]
    public async Task SendAllAsync_SendsOnlyToActiveBotsAndReportsPartialFailure()
    {
        ManagedBot bot1 = CreateBot("bot1");
        ManagedBot bot2 = CreateBot("bot2");
        ManagedBot disabled = CreateBot("bot3") with { Enabled = false, LifecycleState = "disabled" };
        WriteCredentials(bot1, "token1", "chat1");
        WriteCredentials(bot2, "token2", "chat2");
        WriteCredentials(disabled, "token3", "chat3");
        var sender = new RecordingSender(token => token != "token2");
        var service = CreateService([bot1, bot2, disabled], sender);

        TelegramBroadcastResult result = await service.SendAllAsync("Notice", "admin", "127.0.0.1");

        Assert.Equal("partial_failure", result.Outcome);
        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, sender.Messages.Count);
        Assert.DoesNotContain(sender.Messages, message => message.Token == "token3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_RejectsEmptyMessage(string? message)
    {
        var sender = new RecordingSender();
        var service = CreateService([], sender);

        TelegramDeliveryResult result = await service.SendAsync("bot1", message, "admin", "127.0.0.1");

        Assert.Equal("invalid_request", result.Outcome);
        Assert.Empty(sender.Messages);
    }

    private TelegramMessagingService CreateService(IReadOnlyList<ManagedBot> bots, ITelegramMessageSender sender)
    {
        Directory.CreateDirectory(_root);
        return new TelegramMessagingService(
            new Registry(bots), sender,
            new AuditLogger(Path.Combine(_root, "audit.jsonl"), TimeProvider.System),
            _root);
    }

    private ManagedBot CreateBot(string id)
    {
        string path = Path.Combine(_root, id);
        return new ManagedBot(id, id, $"gymbeam-bot-{id}", $"gymbeam-shifts-{id}", path,
            true, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static void WriteCredentials(ManagedBot bot, string token, string chatId)
    {
        Directory.CreateDirectory(bot.InstancePath);
        File.WriteAllText(Path.Combine(bot.InstancePath, ".env"),
            $"GYMBEAM_TELEGRAM_BOT_TOKEN={token}\nGYMBEAM_TELEGRAM_CHAT_ID={chatId}\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Registry(IReadOnlyList<ManagedBot> bots) : IManagedBotRegistry
    {
        public Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bots);
    }

    private sealed class RecordingSender(Func<string, bool>? succeeds = null) : ITelegramMessageSender
    {
        public List<(string Token, string ChatId, string Message)> Messages { get; } = [];
        public Task<bool> SendAsync(string token, string chatId, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add((token, chatId, message));
            return Task.FromResult(succeeds?.Invoke(token) ?? true);
        }
    }
}
