namespace AskYourReports.Reports;

/// <summary>
/// Named date ranges. The LLM picks a name; C# computes the actual dates,
/// so the model never does date arithmetic.
/// </summary>
public static class ReportPeriods
{
    public static readonly IReadOnlyList<string> All =
        ["last_7_days", "last_30_days", "last_90_days", "this_month", "last_month"];

    /// <summary>Returns a half-open range [Start, End).</summary>
    public static (DateOnly Start, DateOnly End) Resolve(string period, DateOnly today)
    {
        var tomorrow = today.AddDays(1);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return period switch
        {
            "last_7_days" => (today.AddDays(-6), tomorrow),
            "last_30_days" => (today.AddDays(-29), tomorrow),
            "last_90_days" => (today.AddDays(-89), tomorrow),
            "this_month" => (firstOfMonth, tomorrow),
            "last_month" => (firstOfMonth.AddMonths(-1), firstOfMonth),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown period"),
        };
    }

    public static string Describe(string period, DateOnly start, DateOnly end) =>
        $"{period.Replace('_', ' ')} ({start:yyyy-MM-dd} to {end.AddDays(-1):yyyy-MM-dd})";
}
