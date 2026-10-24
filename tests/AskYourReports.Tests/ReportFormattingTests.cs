using AskYourReports.Reports;

namespace AskYourReports.Tests;

public class ReportFormattingTests
{
    [Theory]
    [InlineData(280.0, "$280.00")]          // SQLite ROUND returns a double, even for whole amounts
    [InlineData(280L, "$280.00")]
    [InlineData(1237.5, "$1,237.50")]
    [InlineData(0.0, "$0.00")]
    [InlineData(1234567.891, "$1,234,567.89")]
    [InlineData(-12.5, "-$12.50")]
    public void Revenue_is_usd_with_two_decimals(object value, string expected)
    {
        Assert.Equal(expected, ReportFormatting.FormatCell("revenue", value));
    }

    [Theory]
    [InlineData("units", 35L, "35")]
    [InlineData("units", 2523L, "2,523")]
    [InlineData("orders", 1234567L, "1,234,567")]
    [InlineData("product", "Paper Filters (100)", "Paper Filters (100)")]
    [InlineData("month", "2026-05", "2026-05")]
    [InlineData("product", null, "")]
    public void Counts_get_thousands_separators_and_text_is_unchanged(string column, object? value, string expected)
    {
        Assert.Equal(expected, ReportFormatting.FormatCell(column, value));
    }

    [Fact]
    public void Display_rows_format_every_cell_by_its_column()
    {
        var result = new ReportResult("worst_products", "last month", ["product", "category", "units", "revenue"],
            [["Paper Filters (100)", "Equipment", 225L, 1237.5]]);

        Assert.Equal(["Paper Filters (100)", "Equipment", "225", "$1,237.50"], Assert.Single(result.DisplayRows));
    }
}
