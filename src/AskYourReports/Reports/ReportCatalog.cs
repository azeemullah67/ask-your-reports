namespace AskYourReports.Reports;

public enum ParameterKind
{
    Integer,
    Choice,
}

/// <summary>
/// One allowed parameter of a report. The LLM may only supply values that pass these rules.
/// </summary>
public sealed record ParameterSpec(
    string Name,
    ParameterKind Kind,
    string Description,
    object Default,
    int Min = 0,
    int Max = 0,
    IReadOnlyList<string>? Choices = null)
{
    public static ParameterSpec Int(string name, string description, int min, int max, int @default) =>
        new(name, ParameterKind.Integer, description, @default, min, max);

    public static ParameterSpec Choice(string name, string description, IReadOnlyList<string> choices, string @default) =>
        new(name, ParameterKind.Choice, description, @default, Choices: choices);
}

/// <summary>
/// SQL parameters ready to bind, plus a human-readable description of the date range.
/// </summary>
public sealed record BoundQuery(IReadOnlyDictionary<string, object> SqlParameters, string Range);

/// <summary>
/// A whitelisted report: fixed SQL written by a developer, plus the parameters the LLM is allowed to fill.
/// </summary>
public sealed record ReportDefinition(
    string Name,
    string Description,
    IReadOnlyList<ParameterSpec> Parameters,
    string Sql,
    Func<IReadOnlyDictionary<string, object>, DateOnly, BoundQuery> Bind);

/// <summary>
/// The complete list of reports the app can run. Nothing outside this list ever reaches the database.
/// </summary>
public static class ReportCatalog
{
    private static readonly ParameterSpec Period = ParameterSpec.Choice(
        "period", "Date range to report on", ReportPeriods.All, "last_30_days");

    private static readonly ParameterSpec Limit = ParameterSpec.Int(
        "limit", "How many products to return", min: 1, max: 20, @default: 5);

    private static readonly ParameterSpec Months = ParameterSpec.Int(
        "months", "How many calendar months to include, counting the current one", min: 1, max: 12, @default: 6);

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(
            "worst_products",
            "Products with the lowest revenue in a period, including products with no sales.",
            [Period, Limit],
            """
            SELECT p.name AS product, p.category AS category,
                   COALESCE(SUM(s.quantity), 0) AS units,
                   ROUND(COALESCE(SUM(s.quantity * s.unit_price), 0), 2) AS revenue
            FROM products p
            LEFT JOIN sales s ON s.product_id = p.id AND s.sale_date >= $start AND s.sale_date < $end
            GROUP BY p.id
            ORDER BY revenue ASC, p.name
            LIMIT $limit
            """,
            BindPeriodAndLimit),

        new(
            "top_products",
            "Products with the highest revenue in a period.",
            [Period, Limit],
            """
            SELECT p.name AS product, p.category AS category,
                   SUM(s.quantity) AS units,
                   ROUND(SUM(s.quantity * s.unit_price), 2) AS revenue
            FROM sales s
            JOIN products p ON p.id = s.product_id
            WHERE s.sale_date >= $start AND s.sale_date < $end
            GROUP BY p.id
            ORDER BY revenue DESC, p.name
            LIMIT $limit
            """,
            BindPeriodAndLimit),

        new(
            "revenue_by_category",
            "Total units and revenue per product category in a period.",
            [Period],
            """
            SELECT p.category AS category,
                   SUM(s.quantity) AS units,
                   ROUND(SUM(s.quantity * s.unit_price), 2) AS revenue
            FROM sales s
            JOIN products p ON p.id = s.product_id
            WHERE s.sale_date >= $start AND s.sale_date < $end
            GROUP BY p.category
            ORDER BY revenue DESC
            """,
            BindPeriod),

        new(
            "sales_by_region",
            "Number of orders, units and revenue per sales region in a period.",
            [Period],
            """
            SELECT s.region AS region,
                   COUNT(*) AS orders,
                   SUM(s.quantity) AS units,
                   ROUND(SUM(s.quantity * s.unit_price), 2) AS revenue
            FROM sales s
            WHERE s.sale_date >= $start AND s.sale_date < $end
            GROUP BY s.region
            ORDER BY revenue DESC
            """,
            BindPeriod),

        new(
            "monthly_revenue_trend",
            "Units and revenue per calendar month for the last N months (the current month is partial).",
            [Months],
            """
            SELECT substr(s.sale_date, 1, 7) AS month,
                   SUM(s.quantity) AS units,
                   ROUND(SUM(s.quantity * s.unit_price), 2) AS revenue
            FROM sales s
            WHERE s.sale_date >= $start AND s.sale_date < $end
            GROUP BY month
            ORDER BY month
            """,
            BindMonths),
    ];

    public static ReportDefinition? Find(string name) =>
        All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    private static BoundQuery BindPeriod(IReadOnlyDictionary<string, object> values, DateOnly today)
    {
        var period = (string)values["period"];
        var (start, end) = ReportPeriods.Resolve(period, today);
        return new BoundQuery(DateRange(start, end), ReportPeriods.Describe(period, start, end));
    }

    private static BoundQuery BindPeriodAndLimit(IReadOnlyDictionary<string, object> values, DateOnly today)
    {
        var bound = BindPeriod(values, today);
        var parameters = new Dictionary<string, object>(bound.SqlParameters) { ["$limit"] = (int)values["limit"] };
        return bound with { SqlParameters = parameters };
    }

    private static BoundQuery BindMonths(IReadOnlyDictionary<string, object> values, DateOnly today)
    {
        var months = (int)values["months"];
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        var end = today.AddDays(1);
        return new BoundQuery(DateRange(start, end), $"the last {months} month(s), {start:yyyy-MM-dd} to {today:yyyy-MM-dd}");
    }

    private static Dictionary<string, object> DateRange(DateOnly start, DateOnly end) => new()
    {
        ["$start"] = start.ToString("yyyy-MM-dd"),
        ["$end"] = end.ToString("yyyy-MM-dd"),
    };
}
