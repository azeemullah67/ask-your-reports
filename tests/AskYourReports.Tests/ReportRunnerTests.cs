using AskYourReports.Reports;

namespace AskYourReports.Tests;

public class ReportRunnerTests(SeededDatabaseFixture fixture) : IClassFixture<SeededDatabaseFixture>
{
    private readonly ReportRunner _runner = new(fixture.Database, new FixedTimeProvider());

    public static TheoryData<string> AllReports
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var report in ReportCatalog.All)
                data.Add(report.Name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllReports))]
    public void Each_report_returns_rows_on_seed_data(string reportName)
    {
        var result = _runner.Run(Select($$"""{"report":"{{reportName}}"}"""));

        Assert.Equal(reportName, result.Report);
        Assert.NotEmpty(result.Rows);
        Assert.Contains("revenue", result.Columns);
    }

    [Fact]
    public void Worst_products_last_month_is_sorted_by_revenue_ascending_and_uses_the_right_dates()
    {
        var result = _runner.Run(Select("""{"report":"worst_products","parameters":{"period":"last_month","limit":3}}"""));

        var revenue = result.Rows.Select(r => Convert.ToDouble(r[result.Columns.ToList().IndexOf("revenue")])).ToList();
        Assert.Equal(3, revenue.Count);
        Assert.Equal(revenue.OrderBy(v => v), revenue);
        Assert.Contains("2026-05-01 to 2026-05-31", result.Range);
    }

    private static ReportSelection Select(string json)
    {
        Assert.True(ReportSelectionValidator.TryValidate(json, out var selection, out var error), error);
        return selection!;
    }
}
