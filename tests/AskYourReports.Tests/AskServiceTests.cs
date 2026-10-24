using AskYourReports.Llm;
using AskYourReports.Reports;
using Microsoft.Extensions.Logging.Abstractions;

namespace AskYourReports.Tests;

public class AskServiceTests(SeededDatabaseFixture fixture) : IClassFixture<SeededDatabaseFixture>
{
    private AskService CreateService(ILlmClient llm) =>
        new(llm, new ReportRunner(fixture.Database, new FixedTimeProvider()), NullLogger<AskService>.Instance);

    [Fact]
    public async Task Valid_pick_runs_the_report_and_returns_a_plain_english_answer()
    {
        var response = await CreateService(new FakeLlmClient())
            .AskAsync("Which products earned the least revenue last month?", CancellationToken.None);

        Assert.Equal(AskOutcome.Answered, response.Outcome);
        Assert.Equal("worst_products", response.Report);
        Assert.Equal("last_month", response.Parameters!["period"]);
        Assert.Equal(5, response.Result!.Rows.Count);
        Assert.StartsWith("Lowest-revenue products for last month (2026-05-01 to 2026-05-31):", response.Answer);
        Assert.Equal("fake", response.Provider);
    }

    [Theory]
    [InlineData("Sure! Here is the report you asked for.")]
    [InlineData("{\"report\": \"top_products\", ")]
    [InlineData("SELECT name FROM products")]
    public async Task Invalid_json_from_the_model_returns_could_not_map(string modelOutput)
    {
        var response = await CreateService(new FakeLlmClient { ChoiceOverride = modelOutput })
            .AskAsync("Which products earned the least revenue last month?", CancellationToken.None);

        Assert.Equal(AskOutcome.CouldNotMap, response.Outcome);
        Assert.Equal(AskService.CouldNotMapMessage, response.Answer);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task Unknown_report_from_the_model_returns_could_not_map()
    {
        var response = await CreateService(new FakeLlmClient { ChoiceOverride = """{"report":"customer_emails"}""" })
            .AskAsync("List all customer emails", CancellationToken.None);

        Assert.Equal(AskOutcome.CouldNotMap, response.Outcome);
        Assert.Equal(AskService.CouldNotMapMessage, response.Answer);
        Assert.Contains("unknown report 'customer_emails'", response.Detail);
    }

    [Fact]
    public async Task Bad_parameters_from_the_model_return_could_not_map()
    {
        // The keyword fake passes "50" straight through; the C# validator caps limit at 20.
        var response = await CreateService(new FakeLlmClient())
            .AskAsync("Show the top 50 products", CancellationToken.None);

        Assert.Equal(AskOutcome.CouldNotMap, response.Outcome);
        Assert.Contains("between 1 and 20", response.Detail);
    }

    [Fact]
    public async Task Question_with_no_matching_report_returns_could_not_map()
    {
        var response = await CreateService(new FakeLlmClient())
            .AskAsync("What's the weather tomorrow?", CancellationToken.None);

        Assert.Equal(AskOutcome.CouldNotMap, response.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Empty_question_is_rejected_before_calling_the_model(string? question)
    {
        var response = await CreateService(new FakeLlmClient()).AskAsync(question, CancellationToken.None);

        Assert.Equal(AskOutcome.InvalidQuestion, response.Outcome);
    }

    [Fact]
    public async Task Overlong_question_is_rejected_before_calling_the_model()
    {
        var response = await CreateService(new FakeLlmClient())
            .AskAsync(new string('a', AskService.MaxQuestionLength + 1), CancellationToken.None);

        Assert.Equal(AskOutcome.InvalidQuestion, response.Outcome);
    }
}
