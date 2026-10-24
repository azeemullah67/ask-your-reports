using AskYourReports.Reports;

namespace AskYourReports.Tests;

/// <summary>The deterministic summary check, using simulated model output only.</summary>
public class ReportSummariesTests(SeededDatabaseFixture fixture) : IClassFixture<SeededDatabaseFixture>
{
    /// <summary>The same rows as the one live Gemini test (worst_products, last month).</summary>
    private static readonly ReportResult LiveRows = new(
        "worst_products",
        "last month (2026-09-01 to 2026-09-30)",
        ["product", "category", "units", "revenue"],
        [
            ["Chai Spice Blend", "Tea", 35L, 280.0],
            ["Logo T-Shirt", "Merch", 16L, 288.0],
            ["Decaf Beans 500g", "Coffee", 34L, 493.0],
            ["Green Tea Tin", "Tea", 98L, 931.0],
            ["Paper Filters (100)", "Equipment", 225L, 1237.5],
        ]);

    private const string GoodSummary =
        "Last month the products that earned the least revenue were Chai Spice Blend ($280.00), Logo T-Shirt ($288.00), " +
        "Decaf Beans 500g ($493.00), Green Tea Tin ($931.00) and Paper Filters (100) ($1,237.50).";

    /// <summary>Exactly what Gemini returned in the live test.</summary>
    public const string LiveGeminiSummary =
        "Last month, the products that earned the least revenue were Chai Spice Blend with 280 in revenue and Logo T-Shirt with 288 in revenue.";

    [Fact]
    public void A_good_summary_passes()
    {
        Assert.True(ReportSummaries.TryCheck(GoodSummary, LiveRows, out var problem), problem);
    }

    [Fact]
    public void Names_containing_digits_are_not_mistaken_for_numbers_and_the_period_and_row_count_are_allowed()
    {
        var summary = "For last month (2026-09-01 to 2026-09-30), the bottom 5 were Chai Spice Blend ($280.00), Logo T-Shirt " +
                      "($288.00), Decaf Beans 500g ($493.00), Green Tea Tin ($931.00) and Paper Filters (100) ($1,237.50).";

        Assert.True(ReportSummaries.TryCheck(summary, LiveRows, out var problem), problem);
    }

    [Fact]
    public void The_live_gemini_summary_fails()
    {
        Assert.False(ReportSummaries.TryCheck(LiveGeminiSummary, LiveRows, out var problem));
        Assert.Equal("it does not mention Decaf Beans 500g, Green Tea Tin, Paper Filters (100)", problem);
    }

    [Fact]
    public void A_summary_missing_3_of_5_products_fails()
    {
        var summary = "The least revenue came from Chai Spice Blend ($280.00) and Logo T-Shirt ($288.00).";

        Assert.False(ReportSummaries.TryCheck(summary, LiveRows, out var problem));
        Assert.Contains("does not mention Decaf Beans 500g, Green Tea Tin, Paper Filters (100)", problem);
    }

    [Theory]
    [InlineData("280")]
    [InlineData("$280")]
    [InlineData("280.00")]
    public void An_unformatted_amount_fails(string amount)
    {
        var summary = GoodSummary.Replace("$280.00", amount);

        Assert.False(ReportSummaries.TryCheck(summary, LiveRows, out var problem));
        Assert.Equal($"it contains \"{amount}\", which is not a value in the rows", problem);
    }

    [Theory]
    [InlineData(" Together they made $3,229.50.", "$3,229.50")]   // a total the rows do not contain
    [InlineData(" Sales fell 12 percent.", "12")]
    [InlineData(" That is $1,237.51 at most.", "$1,237.51")]
    public void An_invented_number_fails(string addition, string invented)
    {
        Assert.False(ReportSummaries.TryCheck(GoodSummary + addition, LiveRows, out var problem));
        Assert.Equal($"it contains \"{invented}\", which is not a value in the rows", problem);
    }

    [Fact]
    public void With_no_rows_a_plain_no_data_summary_passes_but_an_invented_number_fails()
    {
        var empty = LiveRows with { Rows = [] };

        Assert.True(ReportSummaries.TryCheck("There were no sales last month.", empty, out _));
        Assert.False(ReportSummaries.TryCheck("There were no sales last month, down from $500.00.", empty, out _));
        Assert.Equal("There were no sales for last month (2026-09-01 to 2026-09-30).", ReportSummaries.Template(empty));
    }

    [Fact]
    public void With_more_than_5_rows_names_are_not_all_required_but_numbers_are_still_checked()
    {
        var sixRows = LiveRows with { Rows = [.. LiveRows.Rows, ["Burr Grinder", "Equipment", 30L, 2670.0]] };

        Assert.True(ReportSummaries.TryCheck("The bottom 6 start with Chai Spice Blend ($280.00).", sixRows, out var problem), problem);
        Assert.False(ReportSummaries.TryCheck("The bottom 6 start with Chai Spice Blend (280).", sixRows, out _));
    }

    [Fact]
    public void The_plain_template_lists_every_row_with_formatted_values()
    {
        Assert.Equal(
            "Lowest-revenue products for last month (2026-09-01 to 2026-09-30): Chai Spice Blend ($280.00), " +
            "Logo T-Shirt ($288.00), Decaf Beans 500g ($493.00), Green Tea Tin ($931.00) and Paper Filters (100) ($1,237.50).",
            ReportSummaries.Template(LiveRows));
    }

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
    public void The_plain_template_passes_the_check_for_every_report_on_the_seed_data(string reportName)
    {
        Assert.True(ReportSelectionValidator.TryValidate($$"""{"report":"{{reportName}}"}""", out var selection, out _));
        var result = new ReportRunner(fixture.Database, new FixedTimeProvider()).Run(selection!);

        Assert.True(ReportSummaries.TryCheck(ReportSummaries.Template(result), result, out var problem), problem);
    }
}
