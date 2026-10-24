using AskYourReports.Reports;

namespace AskYourReports.Llm;

/// <summary>Raw model output plus token usage (for logging and cost tracking).</summary>
public sealed record LlmReply(string Text, int InputTokens, int OutputTokens);

/// <summary>
/// The only way the app talks to a language model. Implementations return raw text;
/// callers must validate it (the model is never trusted to produce valid output).
/// Failures are reported as <see cref="LlmException"/> subclasses.
/// </summary>
public interface ILlmClient
{
    /// <summary>Short name shown in the UI and logs, e.g. "fake" or "anthropic".</summary>
    string Name { get; }

    /// <summary>Asks the model to choose one whitelisted report. Expected to return JSON, but may not.</summary>
    Task<LlmReply> ChooseReportAsync(string question, CancellationToken cancellationToken);

    /// <summary>Asks the model to describe report rows in a short plain-English answer.</summary>
    Task<LlmReply> SummarizeAsync(string question, ReportResult result, CancellationToken cancellationToken);
}

public abstract class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The per-day request limit has been used up.</summary>
public sealed class LlmLimitReachedException(string message) : LlmException(message);

/// <summary>A failure worth retrying: rate limiting, provider overload or a network error.</summary>
public sealed class LlmTransientException(string message, Exception? inner = null) : LlmException(message, inner);

/// <summary>The model could not be reached or kept failing (timeouts, errors after retries, bad config).</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : LlmException(message, inner);
