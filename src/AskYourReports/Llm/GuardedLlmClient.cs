using System.Diagnostics;
using AskYourReports.Reports;

namespace AskYourReports.Llm;

/// <summary>
/// Cost and reliability controls around a paid model: a per-day request limit, a per-attempt timeout,
/// retries with exponential backoff for transient failures, and a log line with token counts for every call.
/// Wraps any <see cref="ILlmClient"/>, which is how the tests exercise it with the fake client.
/// </summary>
public sealed class GuardedLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly LlmOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GuardedLlmClient> _logger;

    private readonly object _gate = new();
    private DateOnly _day;
    private int _usedToday;

    public GuardedLlmClient(ILlmClient inner, LlmOptions options, TimeProvider time, ILogger<GuardedLlmClient> logger)
    {
        _inner = inner;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public string Name => _inner.Name;

    public Task<LlmReply> ChooseReportAsync(string question, CancellationToken cancellationToken) =>
        RunAsync("choose_report", token => _inner.ChooseReportAsync(question, token), cancellationToken);

    public Task<LlmReply> SummarizeAsync(string question, ReportResult result, CancellationToken cancellationToken) =>
        RunAsync("summarize", token => _inner.SummarizeAsync(question, result, token), cancellationToken);

    private async Task<LlmReply> RunAsync(
        string operation, Func<CancellationToken, Task<LlmReply>> call, CancellationToken cancellationToken)
    {
        var attempts = _options.MaxRetries + 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var used = ReserveDailySlot();
            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            try
            {
                // WaitAsync enforces the timeout even if the inner client ignores the token.
                var reply = await call(timeout.Token).WaitAsync(timeout.Token);
                _logger.LogInformation(
                    "LLM {Provider} {Operation} attempt {Attempt}: {InputTokens} input + {OutputTokens} output tokens in {ElapsedMs} ms ({Used}/{Limit} calls today)",
                    _inner.Name, operation, attempt, reply.InputTokens, reply.OutputTokens,
                    stopwatch.ElapsedMilliseconds, used, _options.DailyRequestLimit);
                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"No response within {_options.Timeout.TotalSeconds:0.###} s.");
            }
            catch (LlmTransientException ex)
            {
                lastError = ex;
            }

            _logger.LogWarning("LLM {Provider} {Operation} attempt {Attempt}/{Attempts} failed: {Error}",
                _inner.Name, operation, attempt, attempts, lastError.Message);

            if (attempt < attempts)
            {
                var delay = _options.RetryBaseDelay * Math.Pow(2, attempt - 1);
                await Task.Delay(delay, _time, cancellationToken);
            }
        }

        throw new LlmUnavailableException(
            $"The AI service failed {attempts} time(s): {lastError!.Message}", lastError);
    }

    private int ReserveDailySlot()
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            if (today != _day)
            {
                _day = today;
                _usedToday = 0;
            }

            if (_usedToday >= _options.DailyRequestLimit)
            {
                _logger.LogWarning("LLM daily limit of {Limit} calls reached", _options.DailyRequestLimit);
                throw new LlmLimitReachedException(
                    $"The daily AI request limit ({_options.DailyRequestLimit} calls) has been reached. It resets at midnight UTC.");
            }

            return ++_usedToday;
        }
    }
}
