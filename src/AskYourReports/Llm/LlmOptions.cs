namespace AskYourReports.Llm;

/// <summary>
/// The "Llm" section of appsettings.json. The API key is deliberately NOT here:
/// it comes only from dotnet user-secrets or an environment variable.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>"Fake" (default, free, offline), "Anthropic" or "Gemini" (real, paid).</summary>
    public string Provider { get; set; } = "Fake";

    /// <summary>Model ID. Empty means the provider's own default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Requests whose estimated prompt size exceeds this are refused before sending.</summary>
    public int MaxInputTokens { get; set; } = 4_000;

    /// <summary>Hard cap on tokens the model may generate per call (includes its internal reasoning).</summary>
    public int MaxOutputTokens { get; set; } = 1_024;

    /// <summary>Model calls allowed per UTC day, counting every attempt including retries.</summary>
    public int DailyRequestLimit { get; set; } = 100;

    /// <summary>Per-attempt timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Retries after the first attempt, for timeouts and transient errors only.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>First retry waits this long; each later retry doubles it.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A copy with <see cref="Model"/> set to <paramref name="defaultModel"/> if none was configured.</summary>
    public LlmOptions WithDefaultModel(string defaultModel)
    {
        var copy = (LlmOptions)MemberwiseClone();
        if (string.IsNullOrWhiteSpace(copy.Model))
            copy.Model = defaultModel;
        return copy;
    }
}
