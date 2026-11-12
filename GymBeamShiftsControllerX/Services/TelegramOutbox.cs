using Microsoft.Data.Sqlite;

namespace GymBeamShiftsControllerX.Services;

internal sealed class TelegramOutbox : IDisposable
{
    private readonly SqliteConnection _connection;

    public TelegramOutbox(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 2
        };
        _connection = new SqliteConnection(connectionString.ToString());
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS confirmations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    recipient TEXT NOT NULL,
                    message TEXT NOT NULL,
                    due INTEGER NOT NULL,
                    remaining INTEGER NOT NULL,
                    interval_ms INTEGER NOT NULL,
                    cooldown_ms INTEGER NOT NULL,
                    is_repeat INTEGER NOT NULL DEFAULT 0
                )
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public int RemoveOtherRecipients(string recipient)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM confirmations WHERE recipient <> $recipient";
        command.Parameters.AddWithValue("$recipient", recipient);
        return command.ExecuteNonQuery();
    }

    public void Enqueue(string recipient, string message, int count, int intervalMilliseconds, int cooldownMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO confirmations (recipient, message, due, remaining, interval_ms, cooldown_ms)
            VALUES ($recipient, $message, $now, $count, $interval, $cooldown)
            """;
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$interval", intervalMilliseconds);
        command.Parameters.AddWithValue("$cooldown", cooldownMilliseconds);
        command.ExecuteNonQuery();
    }

    public Confirmation? Next(string recipient, long now)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, message, due, remaining, interval_ms, cooldown_ms, is_repeat
            FROM confirmations WHERE recipient = $recipient
            ORDER BY (due > $now), CASE WHEN due <= $now THEN is_repeat ELSE 0 END, due, id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Confirmation(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6))
            : null;
    }

    public void Remove(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM confirmations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Reschedule(long id, long due, int remaining, bool isRepeat)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE confirmations SET due = $due, remaining = $remaining, is_repeat = $isRepeat
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$due", due);
        command.Parameters.AddWithValue("$remaining", remaining);
        command.Parameters.AddWithValue("$isRepeat", isRepeat);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    internal sealed record Confirmation(
        long Id, string Message, long Due, int Remaining, int IntervalMilliseconds, int CooldownMilliseconds, bool IsRepeat);
}
