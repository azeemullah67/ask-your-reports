namespace AskYourReports.Llm;

/// <summary>
/// Picks the LLM client from config. Fake unless Llm:Provider is explicitly "Anthropic".
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<LlmOptions>();
        var config = services.GetRequiredService<IConfiguration>();

        switch (options.Provider.Trim().ToLowerInvariant())
        {
            case "fake":
                return new FakeLlmClient();

            case "anthropic":
                // user-secrets (Development only) or the ANTHROPIC_API_KEY environment variable. Never appsettings.json.
                var apiKey = config["Anthropic:ApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    apiKey = config["ANTHROPIC_API_KEY"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException(
                        "Llm:Provider is 'Anthropic' but no API key was found. Set one with " +
                        "'dotnet user-secrets set Anthropic:ApiKey <key>' or the ANTHROPIC_API_KEY environment variable.");

                return new GuardedLlmClient(
                    new AnthropicLlmClient(apiKey, options),
                    options,
                    services.GetRequiredService<TimeProvider>(),
                    services.GetRequiredService<ILogger<GuardedLlmClient>>());

            default:
                throw new InvalidOperationException($"Unknown Llm:Provider '{options.Provider}'. Use 'Fake' or 'Anthropic'.");
        }
    }
}
