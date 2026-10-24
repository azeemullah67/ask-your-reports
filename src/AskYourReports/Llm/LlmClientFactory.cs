namespace AskYourReports.Llm;

/// <summary>
/// Picks the LLM client from config. Fake unless Llm:Provider is explicitly "Anthropic" or "Gemini".
/// Real clients are always wrapped in <see cref="GuardedLlmClient"/>.
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
                var anthropicKey = RequireApiKey(config, "Anthropic", "Anthropic:ApiKey", "ANTHROPIC_API_KEY");
                return Guard(new AnthropicLlmClient(anthropicKey, options), options, services);

            case "gemini":
                var geminiKey = RequireApiKey(config, "Gemini", "Gemini:ApiKey", "GEMINI_API_KEY");
                return Guard(new GeminiLlmClient(geminiKey, options), options, services);

            default:
                throw new InvalidOperationException(
                    $"Unknown Llm:Provider '{options.Provider}'. Use 'Fake', 'Anthropic' or 'Gemini'.");
        }
    }

    /// <summary>
    /// Reads the key from user-secrets (Development only) or an environment variable. Never from appsettings.json,
    /// and the key itself is never logged or included in error messages.
    /// </summary>
    private static string RequireApiKey(IConfiguration config, string provider, string secretName, string environmentVariable)
    {
        var apiKey = config[secretName];
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = config[environmentVariable];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"Llm:Provider is '{provider}' but no API key was found. Set one with " +
                $"'dotnet user-secrets set {secretName} <key>' or the {environmentVariable} environment variable.");
        return apiKey;
    }

    private static GuardedLlmClient Guard(ILlmClient inner, LlmOptions options, IServiceProvider services) =>
        new(inner, options, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<GuardedLlmClient>>());
}
