using AskYourReports.Data;

namespace AskYourReports.Tests;

/// <summary>A clock frozen at a fixed date (movable by tests), so period-based reports are deterministic.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    public static readonly DateOnly Today = new(2026, 6, 15);

    public DateTimeOffset Now { get; set; } = new(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Collects log messages so tests can check what was logged.</summary>
public sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
            Messages.Add(formatter(state, exception));
    }
}

/// <summary>A seeded SQLite file in the temp folder, shared by the tests in a class and deleted afterwards.</summary>
public sealed class SeededDatabaseFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ayr-test-{Guid.NewGuid():N}.db");

    public SeededDatabaseFixture()
    {
        Database = new SalesDatabase($"Data Source={_path};Pooling=False");
        Database.EnsureSeeded(FixedTimeProvider.Today);
    }

    public SalesDatabase Database { get; }

    public void Dispose() => File.Delete(_path);
}
