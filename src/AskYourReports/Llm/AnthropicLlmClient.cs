using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using AskYourReports.Reports;

namespace AskYourReports.Llm;

/// <summary>
/// Real model via the official Anthropic SDK. Off by default; only created when Llm:Provider is "Anthropic".
/// Always wrapped in <see cref="GuardedLlmClient"/>, which owns the daily limit, timeout and retries,
/// so the SDK's own retries are switched off here to avoid paying for retries twice.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly AnthropicClient _client;
    private readonly LlmOptions _options;

    /// <param name="httpClient">Optional; tests pass one with a fake handler so nothing leaves the machine.</param>
    public AnthropicLlmClient(string apiKey, LlmOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        // MaxRetries = 0: the guard does the retrying. Timeout: the guard's per-attempt timeout is the real one.
        var timeout = options.Timeout + TimeSpan.FromSeconds(5);
        _client = httpClient is null
            ? new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, Timeout = timeout }
            : new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, Timeout = timeout, HttpClient = httpClient };
    }

    public string Name => "anthropic";

    public Task<LlmReply> ChooseReportAsync(string question, CancellationToken cancellationToken) =>
        SendAsync(LlmPrompts.ChooseReportSystem, LlmPrompts.ChooseReportUser(question), LlmPrompts.ChoiceSchema, cancellationToken);

    public Task<LlmReply> SummarizeAsync(string question, ReportResult result, CancellationToken cancellationToken) =>
        SendAsync(LlmPrompts.SummarizeSystem, LlmPrompts.SummarizeUser(question, result), schema: null, cancellationToken);

    private async Task<LlmReply> SendAsync(
        string system, string user, Dictionary<string, JsonElement>? schema, CancellationToken cancellationToken)
    {
        var parameters = BuildRequest(_options, system, user, schema);

        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmTransientException("The provider is rate limiting requests.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new LlmTransientException("The provider returned a server error.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new LlmTransientException("Could not reach the provider.", ex);
        }
        catch (AnthropicApiException ex)
        {
            // 400/401/403/404 etc.: retrying will not help (bad key, bad request, unknown model).
            throw new LlmUnavailableException($"The provider rejected the request: {ex.Message}", ex);
        }

        if (message.StopReason == "refusal")
            throw new LlmUnavailableException("The model declined to answer this request.");
        if (message.StopReason == "max_tokens")
            throw new LlmUnavailableException($"The model hit the output cap of {_options.MaxOutputTokens} tokens.");

        var text = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new LlmReply(text, (int)message.Usage.InputTokens, (int)message.Usage.OutputTokens);
    }

    /// <summary>Builds the request, enforcing the input-token cap before anything is sent.</summary>
    private static MessageCreateParams BuildRequest(
        LlmOptions options, string system, string user, Dictionary<string, JsonElement>? schema)
    {
        var estimated = LlmPrompts.EstimateTokens(system) + LlmPrompts.EstimateTokens(user);
        if (estimated > options.MaxInputTokens)
            throw new LlmUnavailableException(
                $"Request refused before sending: about {estimated} input tokens exceeds the cap of {options.MaxInputTokens}.");

        return new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxOutputTokens,
            System = system,
            Messages = [new UserMessageParam { Content = user }],
            OutputConfig = schema is null
                ? new OutputConfig { Effort = Effort.Low }
                : new OutputConfig { Effort = Effort.Low, Format = new JsonOutputFormat { Schema = schema } },
        };
    }
}
