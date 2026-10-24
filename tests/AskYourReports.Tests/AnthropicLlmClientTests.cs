using System.Net;
using System.Text;
using System.Text.Json;
using AskYourReports.Llm;

namespace AskYourReports.Tests;

/// <summary>
/// The real client, run against a fake HTTP handler: checks the request it would send and how it reads
/// responses and errors. Nothing leaves the machine and no API key is needed.
/// </summary>
public class AnthropicLlmClientTests
{
    private const string OkResponse =
        """
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"{\"report\":\"worst_products\",\"parameters\":{\"period\":\"last_month\"}}"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":812,"output_tokens":37}}
        """;

    [Fact]
    public async Task Sends_a_capped_structured_output_request_and_reads_text_and_token_usage()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkResponse);
        var client = new AnthropicLlmClient("test-key-not-real", new LlmOptions(), new HttpClient(handler));

        var reply = await client.ChooseReportAsync("Which products sold worst last month?", CancellationToken.None);

        Assert.Equal("""{"report":"worst_products","parameters":{"period":"last_month"}}""", reply.Text);
        Assert.Equal(812, reply.InputTokens);
        Assert.Equal(37, reply.OutputTokens);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
        // API version 2026-10-21 sends API keys as a bearer token (older versions used x-api-key).
        Assert.Equal("Bearer test-key-not-real", request.Headers.Authorization?.ToString());

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("claude-opus-5-5", json.GetProperty("model").GetString());
        Assert.Equal(1024, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("user", json.GetProperty("messages")[0].GetProperty("role").GetString());
        var format = json.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var reportNames = format.GetProperty("schema").GetProperty("properties").GetProperty("report").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("worst_products", reportNames);
        Assert.Contains("none", reportNames);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(LlmTransientException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(LlmTransientException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(LlmUnavailableException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(LlmUnavailableException))]
    public async Task Maps_http_errors_to_retryable_or_permanent_failures(HttpStatusCode status, Type expected)
    {
        var handler = new CapturingHandler(status,
            """{"type":"error","error":{"type":"api_error","message":"simulated"}}""");
        var client = new AnthropicLlmClient("test-key-not-real", new LlmOptions(), new HttpClient(handler));

        var ex = await Record.ExceptionAsync(() => client.ChooseReportAsync("top products", CancellationToken.None));

        Assert.IsType(expected, ex);
        Assert.Single(handler.Requests); // the SDK's own retries are off; the guard owns retrying
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_anything_is_sent()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkResponse);
        var client = new AnthropicLlmClient("test-key-not-real", new LlmOptions { MaxInputTokens = 50 }, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(
            () => client.ChooseReportAsync("top products", CancellationToken.None));

        Assert.Contains("exceeds the cap of 50", ex.Message);
        Assert.Empty(handler.Requests);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
