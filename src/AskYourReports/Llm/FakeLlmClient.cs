using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskYourReports.Reports;

namespace AskYourReports.Llm;

/// <summary>
/// A free, deterministic stand-in for a real model. No network calls.
/// It chooses reports by keyword matching and writes summaries from a template,
/// which is enough to demo and test the whole pipeline.
/// </summary>
public sealed partial class FakeLlmClient : ILlmClient
{
    public string Name => "fake";

    private int _calls;

    /// <summary>When set, returned verbatim instead of the keyword-based choice (lets tests send bad JSON etc.).</summary>
    public string? ChoiceOverride { get; init; }

    /// <summary>Simulated latency per call (lets tests trigger timeouts).</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>The first N calls throw <see cref="LlmTransientException"/> (lets tests exercise retries).</summary>
    public int TransientFailures { get; init; }

    /// <summary>Number of calls received, including failed ones.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async Task<LlmReply> ChooseReportAsync(string question, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        var text = ChoiceOverride ?? ChooseByKeywords(question);
        return new LlmReply(text, EstimateTokens(question), EstimateTokens(text));
    }

    public async Task<LlmReply> SummarizeAsync(string question, ReportResult result, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        var text = Summarize(result);
        return new LlmReply(text, EstimateTokens(question), EstimateTokens(text));
    }

    private async Task SimulateAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        if (call <= TransientFailures)
            throw new LlmTransientException($"Simulated transient failure {call} of {TransientFailures}.");
    }

    private static string ChooseByKeywords(string question)
    {
        var q = question.ToLowerInvariant();

        string? report =
            Has(q, "worst|least|lowest|bottom|poorly|weakest") ? "worst_products" :
            Has(q, "trend|monthly|per month|by month|over time") ? "monthly_revenue_trend" :
            Has(q, "category|categories") ? "revenue_by_category" :
            Has(q, "region|regions") ? "sales_by_region" :
            Has(q, "top|best|most|highest|bestselling") ? "top_products" :
            null;

        if (report is null)
            return """{"report":"none"}""";

        var parameters = new Dictionary<string, object>();
        if (report == "monthly_revenue_trend")
        {
            if (FirstNumber(MonthsPattern(), q) is { } months)
                parameters["months"] = months;
        }
        else
        {
            parameters["period"] = PeriodFrom(q);
            if (report is "worst_products" or "top_products" && FirstNumber(LimitPattern(), q) is { } limit)
                parameters["limit"] = limit;
        }

        return JsonSerializer.Serialize(new { report, parameters });
    }

    private static string PeriodFrom(string q) =>
        Has(q, "last month|previous month") ? "last_month" :
        Has(q, "this month") ? "this_month" :
        Has(q, "week|7 days") ? "last_7_days" :
        Has(q, "quarter|90 days|3 months|three months") ? "last_90_days" :
        "last_30_days";

    private static string Summarize(ReportResult result)
    {
        if (result.Rows.Count == 0)
            return $"There were no sales for {result.Range}.";

        var heading = result.Report switch
        {
            "worst_products" => "Lowest-revenue products",
            "top_products" => "Highest-revenue products",
            "revenue_by_category" => "Revenue by category",
            "sales_by_region" => "Revenue by region",
            "monthly_revenue_trend" => "Revenue by month",
            _ => "Results",
        };

        var revenueIndex = result.Columns.ToList().IndexOf("revenue");
        var items = result.Rows.Take(5).Select(row =>
            revenueIndex < 0
                ? $"{row[0]}"
                : $"{row[0]} (${Convert.ToDouble(row[revenueIndex]).ToString("N2", CultureInfo.InvariantCulture)})");

        var more = result.Rows.Count > 5 ? $", and {result.Rows.Count - 5} more" : "";
        return $"{heading} for {result.Range}: {string.Join(", ", items)}{more}.";
    }

    private static bool Has(string text, string alternatives) =>
        Regex.IsMatch(text, $@"\b({alternatives})\b");

    private static int? FirstNumber(Regex pattern, string text) =>
        pattern.Match(text) is { Success: true } match ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Rough token estimate (about 4 characters per token) so the fake reports plausible usage.</summary>
    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    [GeneratedRegex(@"\b(?:top|bottom|worst|best|lowest|highest)\s+(?<n>\d{1,4})\b")]
    private static partial Regex LimitPattern();

    [GeneratedRegex(@"\b(?<n>\d{1,4})\s+months?\b")]
    private static partial Regex MonthsPattern();
}
