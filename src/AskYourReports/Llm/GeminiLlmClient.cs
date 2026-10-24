using System.Text.Json;
using AskYourReports.Reports;
using Google.GenAI;
using Google.GenAI.Types;

namespace AskYourReports.Llm;

/// <summary>
/// Real model via the official Google GenAI SDK (Gemini Developer API). Off by default; only created when
/// Llm:Provider is "Gemini". Always wrapped in <see cref="GuardedLlmClient"/>, which owns the daily limit,
/// timeout and retries, so the SDK is limited to a single attempt here.
/// </summary>
public sealed class GeminiLlmClient : ILlmClient
{
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>Suggested in error messages if the default model is not available to the key.</summary>
    public const string FallbackModel = "gemini-3.8-flash";

    private readonly Client _client;
    private readonly LlmOptions _options;

    /// <param name="httpClient">Optional; tests pass one with a fake handler so nothing leaves the machine.</param>
    public GeminiLlmClient(string apiKey, LlmOptions options, HttpClient? httpClient = null)
    {
        _options = options.WithDefaultModel(DefaultModel);
        var httpOptions = new HttpOptions
        {
            // The guard's per-attempt timeout is the real one; this is only a backstop.
            Timeout = (int)(options.Timeout + TimeSpan.FromSeconds(5)).TotalMilliseconds,
            // One attempt: the guard does the retrying, so retries are never doubled.
            RetryOptions = new HttpRetryOptions { Attempts = 1 },
        };
        var clientOptions = httpClient is null ? null : new ClientOptions { HttpClientFactory = () => httpClient };
        _client = new Client(apiKey: apiKey, httpOptions: httpOptions, clientOptions: clientOptions);
    }

    public string Name => "gemini";

    public Task<LlmReply> ChooseReportAsync(string question, CancellationToken cancellationToken) =>
        SendAsync(LlmPrompts.ChooseReportSystem, LlmPrompts.ChooseReportUser(question), LlmPrompts.ChoiceSchema, cancellationToken);

    public Task<LlmReply> SummarizeAsync(string question, ReportResult result, CancellationToken cancellationToken) =>
        SendAsync(LlmPrompts.SummarizeSystem, LlmPrompts.SummarizeUser(question, result), schema: null, cancellationToken);

    private async Task<LlmReply> SendAsync(
        string system, string user, Dictionary<string, JsonElement>? schema, CancellationToken cancellationToken)
    {
        var estimated = LlmPrompts.EstimateTokens(system) + LlmPrompts.EstimateTokens(user);
        if (estimated > _options.MaxInputTokens)
            throw new LlmUnavailableException(
                $"Request refused before sending: about {estimated} input tokens exceeds the cap of {_options.MaxInputTokens}.");

        var config = new GenerateContentConfig
        {
            SystemInstruction = new Content { Parts = [new Part { Text = system }] },
            MaxOutputTokens = _options.MaxOutputTokens,
        };
        if (schema is not null)
        {
            // JSON Schema mode supports every keyword the choice schema uses (type, properties, enum,
            // required, additionalProperties); the C# validator still checks the result.
            config.ResponseMimeType = "application/json";
            config.ResponseJsonSchema = schema;
        }

        GenerateContentResponse response;
        try
        {
            response = await _client.Models.GenerateContentAsync(
                model: _options.Model, contents: user, config: config, cancellationToken: cancellationToken);
        }
        catch (ServerError ex)
        {
            throw new LlmTransientException("The provider returned a server error.", ex);
        }
        catch (ClientError ex) when (ex.StatusCode == 429)
        {
            throw new LlmTransientException("The provider is rate limiting requests.", ex);
        }
        catch (ClientError ex)
        {
            // Retrying will not help with these, and retrying would cost more.
            throw new LlmUnavailableException(ex.StatusCode switch
            {
                401 or 403 => "The provider rejected the API key (check GEMINI_API_KEY or Gemini:ApiKey).",
                402 => "The provider reported a billing problem (402 Payment Required), for example an empty credit balance. Check billing for your Gemini API key.",
                404 => $"Model '{_options.Model}' was not found for this key. Try --Llm:Model={FallbackModel}.",
                _ => $"The provider rejected the request ({ex.StatusCode}): {ex.Message}",
            }, ex);
        }
        catch (HttpRequestException ex) // network failure; ApiException also derives from this, so it is caught last
        {
            throw new LlmTransientException("Could not reach the provider.", ex);
        }

        if (response.PromptFeedback?.BlockReason is { } blockReason)
            throw new LlmUnavailableException($"The provider blocked this request ({blockReason}).");

        var candidate = response.Candidates?.FirstOrDefault()
            ?? throw new LlmUnavailableException("The provider returned no answer.");

        if (candidate.FinishReason == FinishReason.MaxTokens)
            throw new LlmUnavailableException($"The model hit the output cap of {_options.MaxOutputTokens} tokens.");
        if (candidate.FinishReason is { } reason && reason != FinishReason.Stop)
            throw new LlmUnavailableException($"The provider stopped the answer ({reason}).");

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmUnavailableException("The provider returned an empty answer.");

        // Output tokens include the model's thinking tokens, which are billed as output.
        var usage = response.UsageMetadata;
        var outputTokens = (usage?.CandidatesTokenCount ?? 0) + (usage?.ThoughtsTokenCount ?? 0);
        return new LlmReply(text, usage?.PromptTokenCount ?? 0, outputTokens);
    }
}
