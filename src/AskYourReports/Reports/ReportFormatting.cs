using System.Globalization;

namespace AskYourReports.Reports;

/// <summary>
/// The one place report values are formatted for people. The page, the fake client, the summary prompt and the
/// summary check all use these strings, so "$280.00" means the same thing everywhere.
/// Revenue is in US dollars (the sample data's currency) with two decimals; counts get thousands separators.
/// </summary>
public static class ReportFormatting
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string FormatCell(string column, object? value) => value switch
    {
        null => "",
        _ when column == "revenue" => Money(Convert.ToDecimal(value, Invariant)),
        int or long => Convert.ToInt64(value, Invariant).ToString("N0", Invariant),
        double or float or decimal => Convert.ToDecimal(value, Invariant).ToString("N2", Invariant),
        _ => Convert.ToString(value, Invariant) ?? "",
    };

    public static string Money(decimal amount) =>
        amount < 0 ? "-$" + (-amount).ToString("N2", Invariant) : "$" + amount.ToString("N2", Invariant);

    public static IReadOnlyList<string[]> FormatRows(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows) =>
        rows.Select(row => row.Select((value, i) => FormatCell(columns[i], value)).ToArray()).ToList();
}
