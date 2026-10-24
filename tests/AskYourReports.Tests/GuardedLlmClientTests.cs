using AskYourReports.Llm;
using AskYourReports.Reports;
using Microsoft.Extensions.Logging.Abstractions;

namespace AskYourReports.Tests;

/// <summary>The real client's cost and reliability controls, exercised with the fake client simulating failures.</summary>
public class GuardedLlmClientTests(SeededDatabaseFixture fixture) : IClassFixture<SeededDatabaseFixture>
{
    private const string Question = "Which products earned the least revenue last month?";

    private readonly FixedTimeProvider _time = new();
    private readonly ListLogger<GuardedLlmClient> _logger = new();

    private static LlmOptions FastOptions(int dailyLimit = 100) => new()
    {
        DailyRequestLimit = dailyLimit,
        Timeout = TimeSpan.FromMilliseconds(100),
        MaxRetries = 2,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };

    private GuardedLlmClient Guard(FakeLlmClient fake, LlmOptions options) => new(fake, options, _time, _logger);

    [Fact]
    public async Task Timeout_is_retried_twice_then_reported_as_unavailable()
    {
        var fake = new FakeLlmClient { Delay = TimeSpan.FromSeconds(5) };

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(
            () => Guard(fake, FastOptions()).ChooseReportAsync(Question, CancellationToken.None));

        Assert.Equal(3, fake.Calls);
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("No response within", ex.Message);
    }

    [Fact]
    public async Task Transient_errors_are_retried_with_backoff_and_can_then_succeed()
    {
        var fake = new FakeLlmClient { TransientFailures = 2 };

        var reply = await Guard(fake, FastOptions()).ChooseReportAsync(Question, CancellationToken.None);

        Assert.Contains("worst_products", reply.Text);
        Assert.Equal(3, fake.Calls);
        Assert.Equal(2, _logger.Messages.Count(m => m.Contains("failed: Simulated transient failure")));
    }

    [Fact]
    public async Task Persistent_errors_give_up_after_two_retries()
    {
        var fake = new FakeLlmClient { TransientFailures = 10 };

        await Assert.ThrowsAsync<LlmUnavailableException>(
            () => Guard(fake, FastOptions()).ChooseReportAsync(Question, CancellationToken.None));

        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task Daily_limit_blocks_further_calls_and_resets_the_next_utc_day()
    {
        var fake = new FakeLlmClient();
        var guard = Guard(fake, FastOptions(dailyLimit: 2));

        await guard.ChooseReportAsync(Question, CancellationToken.None);
        await guard.ChooseReportAsync(Question, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<LlmLimitReachedException>(
            () => guard.ChooseReportAsync(Question, CancellationToken.None));

        Assert.Contains("daily AI request limit (2 calls)", ex.Message);
        Assert.Equal(2, fake.Calls); // the blocked call never reached the model

        _time.Now = _time.Now.AddDays(1);
        await guard.ChooseReportAsync(Question, CancellationToken.None);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task Retries_count_towards_the_daily_limit()
    {
        var fake = new FakeLlmClient { TransientFailures = 1 };

        await Assert.ThrowsAsync<LlmLimitReachedException>(
            () => Guard(fake, FastOptions(dailyLimit: 1)).ChooseReportAsync(Question, CancellationToken.None));

        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Each_successful_call_logs_its_token_counts()
    {
        var reply = await Guard(new FakeLlmClient(), FastOptions()).ChooseReportAsync(Question, CancellationToken.None);

        var line = Assert.Single(_logger.Messages);
        Assert.Contains($"{reply.InputTokens} input + {reply.OutputTokens} output tokens", line);
        Assert.Contains("(1/100 calls today)", line);
    }

    [Fact]
    public async Task Ask_returns_a_clear_message_when_the_daily_limit_is_reached()
    {
        var service = new AskService(
            Guard(new FakeLlmClient(), FastOptions(dailyLimit: 1)),
            new ReportRunner(fixture.Database, new FixedTimeProvider()),
            NullLogger<AskService>.Instance);

        // First question: choosing the report uses the only call, so the rows come back without a written summary.
        var first = await service.AskAsync(Question, CancellationToken.None);
        Assert.Equal(AskOutcome.Answered, first.Outcome);
        Assert.NotEmpty(first.Result!.Rows);
        Assert.Contains("summary is unavailable", first.Answer);

        var second = await service.AskAsync(Question, CancellationToken.None);
        Assert.Equal(AskOutcome.LimitReached, second.Outcome);
        Assert.Contains("daily AI request limit", second.Answer);
    }

    [Fact]
    public async Task Ask_returns_a_clear_message_when_the_model_times_out()
    {
        var service = new AskService(
            Guard(new FakeLlmClient { Delay = TimeSpan.FromSeconds(5) }, FastOptions()),
            new ReportRunner(fixture.Database, new FixedTimeProvider()),
            NullLogger<AskService>.Instance);

        var response = await service.AskAsync(Question, CancellationToken.None);

        Assert.Equal(AskOutcome.Unavailable, response.Outcome);
        Assert.Equal("The AI service is unavailable right now. Please try again shortly.", response.Answer);
    }
}
