using AskYourReports.Reports;

namespace AskYourReports.Tests;

public class ReportSelectionValidatorTests
{
    [Fact]
    public void Valid_pick_is_accepted_and_missing_parameters_get_defaults()
    {
        var ok = ReportSelectionValidator.TryValidate(
            """{"report":"worst_products","parameters":{"period":"last_month"}}""", out var selection, out _);

        Assert.True(ok);
        Assert.Equal("worst_products", selection!.Report.Name);
        Assert.Equal("last_month", selection.Values["period"]);
        Assert.Equal(5, selection.Values["limit"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"report\":\"top_products\"")]
    [InlineData("[\"top_products\"]")]
    [InlineData("SELECT * FROM sales; DROP TABLE sales;")]
    public void Invalid_json_is_rejected(string json)
    {
        Assert.False(ReportSelectionValidator.TryValidate(json, out var selection, out var error));
        Assert.Null(selection);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("""{"report":"delete_everything"}""")]
    [InlineData("""{"report":"none"}""")]
    [InlineData("""{"report":"Top_Products"}""")]
    [InlineData("""{"parameters":{"limit":5}}""")]
    public void Unknown_report_is_rejected(string json)
    {
        Assert.False(ReportSelectionValidator.TryValidate(json, out _, out var error));
        Assert.True(error.Contains("unknown report") || error.Contains("'report' field"), error);
    }

    [Theory]
    [InlineData("""{"report":"top_products","parameters":{"limit":500}}""", "between 1 and 20")]
    [InlineData("""{"report":"top_products","parameters":{"limit":0}}""", "between 1 and 20")]
    [InlineData("""{"report":"top_products","parameters":{"limit":"5"}}""", "whole number")]
    [InlineData("""{"report":"top_products","parameters":{"limit":2.5}}""", "whole number")]
    [InlineData("""{"report":"top_products","parameters":{"period":"forever"}}""", "must be one of")]
    [InlineData("""{"report":"monthly_revenue_trend","parameters":{"months":13}}""", "between 1 and 12")]
    [InlineData("""{"report":"sales_by_region","parameters":{"limit":5}}""", "has no parameter 'limit'")]
    [InlineData("""{"report":"top_products","parameters":{"sql":"DROP TABLE sales"}}""", "has no parameter 'sql'")]
    [InlineData("""{"report":"top_products","parameters":[5]}""", "not an object")]
    [InlineData("""{"report":"top_products","sql":"SELECT 1"}""", "unexpected or duplicate field")]
    public void Bad_parameters_are_rejected(string json, string expectedError)
    {
        Assert.False(ReportSelectionValidator.TryValidate(json, out _, out var error));
        Assert.Contains(expectedError, error);
    }
}
