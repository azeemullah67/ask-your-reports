using System.Net;
using System.Text;
using System.Text.Json;
using AskYourReports.Llm;
using AskYourReports.Reports;

namespace AskYourReports.Tests;

/// <summary>
/// The Gemini client, run against a fake HTTP handler: checks the request it would send and how it reads
/// responses and errors. Nothing leaves the machine and no API key is needed.
/// </summary>
public class GeminiLlmClientTests
{
    private const string Question = "Which products earned the least revenue last month?";

    private const string ChoiceResponse =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"report\":\"worst_products\",\"parameters\":{\"period\":\"last_month\"}}"}]},
          "finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":640,"candidatesTokenCount":21,"thoughtsTokenCount":15,"totalTokenCount":676}}
        """;

    private const string SummaryResponse =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"Chai Spice Blend earned the least, at $280.00."}]},
          "finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":300,"candidatesTokenCount":12}}
        """;

    private static (GeminiLlmClient Client, FakeHandler Handler) Create(LlmOptions? options = null, params Func<HttpResponseMessage>[] responses)
    {
        var handler = new FakeHandler(responses);
        return (new GeminiLlmClient("test-key-not-real", options ?? new LlmOptions(), new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Choose_report_sends_a_json_schema_request_and_reads_the_reply_and_token_usage()
    {
        var (client, handler) = Create(responses: Ok(ChoiceResponse));

        var reply = await client.ChooseReportAsync(Question, CancellationToken.None);

        Assert.Equal("""{"report":"worst_products","parameters":{"period":"last_month"}}""", reply.Text);
        Assert.True(ReportSelectionValidator.TryValidate(reply.Text, out _, out _));
        Assert.Equal(640, reply.InputTokens);
        Assert.Equal(36, reply.OutputTokens); // 21 answer + 15 thinking tokens

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/models/gemini-3.5-flash-lite:generateContent", request.Uri.AbsolutePath);
        Assert.Equal("test-key-not-real", request.ApiKey);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Contains(Question, body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Contains("pre-approved report", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());

        var generation = body.GetProperty("generationConfig");
        Assert.Equal(1024, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        var schema = generation.GetProperty("responseJsonSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var reportNames = schema.GetProperty("properties").GetProperty("report").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([.. ReportCatalog.All.Select(r => r.Name), "none"], reportNames);
    }

    [Fact]
    public async Task Summarize_asks_for_plain_text_from_display_formatted_rows()
    {
        var (client, handler) = Create(responses: Ok(SummaryResponse));
        var result = new ReportResult("worst_products", "last month", ["product", "units", "revenue"], [["Chai Spice Blend", 1035L, 280.0]]);

        var reply = await client.SummarizeAsync(Question, result, CancellationToken.None);

        Assert.Equal("Chai Spice Blend earned the least, at $280.00.", reply.Text);
        Assert.Equal(12, reply.OutputTokens); // no thinking tokens reported

        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        var generation = body.GetProperty("generationConfig");
        Assert.False(generation.TryGetProperty("responseMimeType", out _));
        Assert.False(generation.TryGetProperty("responseJsonSchema", out _));

        // The model sees the same strings as the page, not raw numbers.
        var userText = body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("""["Chai Spice Blend","1,035","$280.00"]""", userText);
        Assert.Contains("Row count: 1", userText);
        Assert.DoesNotContain("280]", userText);
        var systemText = body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("If there are 5 rows or fewer, name every row", systemText);
        Assert.Contains("\"$1,237.50\"", systemText);
    }

    [Fact]
    public async Task A_simulated_gemini_summary_like_the_live_one_falls_back_to_the_plain_summary()
    {
        using var database = new SeededDatabaseFixture();
        var summary = JsonSerializer.Serialize(ReportSummariesTests.LiveGeminiSummary);
        var (client, handler) = Create(responses:
        [
            Ok(ChoiceResponse),
            Ok($$"""{"candidates":[{"content":{"parts":[{"text":{{summary}}}]},"finishReason":"STOP"}]}"""),
        ]);
        var service = new AskService(client, new ReportRunner(database.Database, new FixedTimeProvider()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AskService>.Instance);

        var response = await service.AskAsync(Question, CancellationToken.None);

        Assert.Equal("worst_products", response.Report);
        Assert.False(response.SummaryChecked);
        Assert.Equal(ReportSummaries.Template(response.Result!), response.Answer);
        Assert.Equal(2, handler.Requests.Count); // no retry of the summary call
    }

    [Fact]
    public async Task Configured_model_overrides_the_default()
    {
        var (client, handler) = Create(new LlmOptions { Model = GeminiLlmClient.FallbackModel }, Ok(ChoiceResponse));

        await client.ChooseReportAsync(Question, CancellationToken.None);

        Assert.EndsWith("/models/gemini-3.8-flash:generateContent", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(429, typeof(LlmTransientException), "rate limiting")]
    [InlineData(500, typeof(LlmTransientException), "server error")]
    [InlineData(503, typeof(LlmTransientException), "server error")]
    [InlineData(400, typeof(LlmUnavailableException), "rejected the request (400)")]
    [InlineData(401, typeof(LlmUnavailableException), "rejected the API key")]
    [InlineData(402, typeof(LlmUnavailableException), "empty credit balance")]
    [InlineData(403, typeof(LlmUnavailableException), "rejected the API key")]
    [InlineData(404, typeof(LlmUnavailableException), "Try --Llm:Model=gemini-3.8-flash")]
    public async Task Maps_http_errors_to_retryable_or_permanent_failures(int status, Type expected, string message)
    {
        var (client, handler) = Create(responses: Error(status));

        var ex = await Record.ExceptionAsync(() => client.ChooseReportAsync(Question, CancellationToken.None));

        Assert.IsType(expected, ex);
        Assert.Contains(message, ex!.Message);
        Assert.DoesNotContain("test-key-not-real", ex.Message);
        Assert.Single(handler.Requests); // the SDK's own retries are off; the guard owns retrying
    }

    [Fact]
    public async Task Network_failure_is_retryable()
    {
        var (client, _) = Create(responses: () => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<LlmTransientException>(() => client.ChooseReportAsync(Question, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"},"usageMetadata":{"promptTokenCount":40}}""", "blocked this request (SAFETY)")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}],"usageMetadata":{"promptTokenCount":40}}""", "stopped the answer (SAFETY)")]
    [InlineData("""{"candidates":[],"usageMetadata":{"promptTokenCount":40}}""", "returned no answer")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"{\"report\":"}]},"finishReason":"MAX_TOKENS"}]}""", "output cap of 1024 tokens")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"  "}]},"finishReason":"STOP"}]}""", "empty answer")]
    public async Task Blocked_empty_or_truncated_responses_become_unavailable(string responseBody, string message)
    {
        var (client, _) = Create(responses: Ok(responseBody));

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChooseReportAsync(Question, CancellationToken.None));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_anything_is_sent()
    {
        var (client, handler) = Create(new LlmOptions { MaxInputTokens = 50 }, Ok(ChoiceResponse));

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChooseReportAsync(Question, CancellationToken.None));

        Assert.Contains("exceeds the cap of 50", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Guard_retries_a_rate_limited_gemini_call_and_logs_its_tokens()
    {
        var options = new LlmOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        var (client, handler) = Create(options, Error(429), Ok(ChoiceResponse));
        var logger = new ListLogger<GuardedLlmClient>();
        var guard = new GuardedLlmClient(client, options, new FixedTimeProvider(), logger);

        var reply = await guard.ChooseReportAsync(Question, CancellationToken.None);

        Assert.Equal(36, reply.OutputTokens);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(logger.Messages, m => m.Contains("gemini choose_report attempt 2: 640 input + 36 output tokens"));
    }

    private static Func<HttpResponseMessage> Ok(string body) => () => Json(HttpStatusCode.OK, body);

    private static Func<HttpResponseMessage> Error(int status) => () => Json((HttpStatusCode)status,
        $$$"""{"error":{"code":{{{status}}},"message":"simulated error","status":"SIMULATED"}}""");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(Uri Uri, string? ApiKey, string Body);

    /// <summary>Records each request and plays back the given responses in order (the last one repeats).</summary>
    private sealed class FakeHandler(Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            Requests.Add(new CapturedRequest(request.RequestUri!, apiKey, body));
            return responses[Math.Min(Requests.Count, responses.Length) - 1]();
        }
    }
}
