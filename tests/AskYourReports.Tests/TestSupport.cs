using AskYourReports.Data;

namespace AskYourReports.Tests;

/// <summary>A clock frozen at a fixed date, so period-based reports are deterministic.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public static readonly DateOnly Today = new(2026, 6, 15);

    public FixedTimeProvider() : this(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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
