using System.Text.RegularExpressions;

namespace AskYourReports.Reports;

/// <summary>
/// A deterministic plain summary of report rows, and a check that a model-written summary sticks to the rows.
/// Both work on <see cref="ReportResult.DisplayRows"/>, the same strings the page shows.
/// </summary>
public static partial class ReportSummaries
{
    /// <summary>With this many rows or fewer, a summary must name every row.</summary>
    public const int NameEveryRowUpTo = 5;

    /// <summary>Plain summary built in C#: used by the fake client and as the fallback when a model summary fails the check.</summary>
    public static string Template(ReportResult result)
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
        var items = result.DisplayRows.Select(row => revenueIndex < 0 ? row[0] : $"{row[0]} ({row[revenueIndex]})").ToList();
        var list = items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
        return $"{heading} for {result.Range}: {list}.";
    }

    /// <summary>
    /// Checks a model-written summary against the rows: with <see cref="NameEveryRowUpTo"/> rows or fewer, every row's
    /// name (first column) must appear; and every number in the summary must exactly match a formatted value in the
    /// rows. The only other numbers allowed are the period text copied exactly and the row count ("top 5").
    /// </summary>
    public static bool TryCheck(string summary, ReportResult result, out string problem)
    {
        problem = "";
        var rows = result.DisplayRows;

        if (rows.Count <= NameEveryRowUpTo)
        {
            var missing = rows.Select(r => r[0]).Where(name => !summary.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
            {
                problem = $"it does not mention {string.Join(", ", missing)}";
                return false;
            }
        }

        // Remove the period and every text value (names can contain digits, e.g. "Paper Filters (100)") before
        // looking for numbers, so only free-standing numbers are checked.
        var text = summary.Replace(result.Range, " ", StringComparison.OrdinalIgnoreCase);
        var textCells = rows.SelectMany(row => row.Where((_, i) => IsTextColumn(result, i)));
        foreach (var value in textCells.Where(v => v.Length > 0).Distinct().OrderByDescending(v => v.Length))
            text = text.Replace(value, " ", StringComparison.OrdinalIgnoreCase);

        var allowed = rows.SelectMany(row => row.Where((_, i) => !IsTextColumn(result, i))).ToHashSet(StringComparer.Ordinal);
        allowed.Add(rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (Match match in NumberPattern().Matches(text))
        {
            var number = match.Value.TrimEnd(',');
            if (!allowed.Contains(number))
            {
                problem = $"it contains \"{number}\", which is not a value in the rows";
                return false;
            }
        }

        return true;
    }

    private static bool IsTextColumn(ReportResult result, int column) =>
        result.Rows.Count > 0 && result.Rows[0][column] is string;

    // A number with an optional dollar sign, thousands separators and decimals: 280, $280.00, 1,237.50
    [GeneratedRegex(@"\$?\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
