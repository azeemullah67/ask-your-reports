using System.Text.Json.Serialization;
using AskYourReports.Llm;
using AskYourReports.Reports;

namespace AskYourReports;

[JsonConverter(typeof(JsonStringEnumConverter<AskOutcome>))]
public enum AskOutcome
{
    Answered,
    InvalidQuestion,
    CouldNotMap,
    LimitReached,
    Unavailable,
}

public sealed record AskResponse(
    AskOutcome Outcome,
    string Answer,
    string Provider,
    string? Detail = null,
    string? Report = null,
    IReadOnlyDictionary<string, object>? Parameters = null,
    ReportResult? Result = null,
    // true: the AI summary passed the check; false: it failed and Answer is a plain C# summary; null: no AI summary.
    bool? SummaryChecked = null);

/// <summary>
/// The whole pipeline: question -> model picks a report -> C# validates -> SQL runs -> model summarizes.
/// Every failure becomes a clear <see cref="AskResponse"/> instead of an exception.
/// </summary>
public sealed class AskService
{
    public const int MaxQuestionLength = 300;
    public const string CouldNotMapMessage = "I couldn't map that question to one of the available reports.";

    private readonly ILlmClient _llm;
    private readonly ReportRunner _runner;
    private readonly ILogger<AskService> _logger;

    public AskService(ILlmClient llm, ReportRunner runner, ILogger<AskService> logger)
    {
        _llm = llm;
        _runner = runner;
        _logger = logger;
    }

    public async Task<AskResponse> AskAsync(string? question, CancellationToken cancellationToken)
    {
        question = question?.Trim();
        if (string.IsNullOrEmpty(question))
            return Respond(AskOutcome.InvalidQuestion, "Please type a question.");
        if (question.Length > MaxQuestionLength)
            return Respond(AskOutcome.InvalidQuestion, $"Please keep questions under {MaxQuestionLength} characters.");

        LlmReply choice;
        try
        {
            choice = await _llm.ChooseReportAsync(question, cancellationToken);
        }
        catch (LlmException ex)
        {
            return FromLlmFailure(ex);
        }

        if (!ReportSelectionValidator.TryValidate(choice.Text, out var selection, out var error))
        {
            _logger.LogWarning("Could not map question: {Reason}. Model output: {Output}", error, Truncate(choice.Text));
            return Respond(AskOutcome.CouldNotMap, CouldNotMapMessage, detail: error);
        }

        var result = _runner.Run(selection!);

        string answer;
        string? detail = null;
        bool? summaryChecked = null;
        try
        {
            var summary = (await _llm.SummarizeAsync(question, result, cancellationToken)).Text.Trim();
            if (ReportSummaries.TryCheck(summary, result, out var problem))
            {
                answer = summary;
                summaryChecked = true;
            }
            else
            {
                // Never show text that disagrees with the rows. No retry: a second model call costs money.
                _logger.LogWarning("AI summary failed the check: {Problem}. Model output: {Output}", problem, Truncate(summary));
                answer = ReportSummaries.Template(result);
                detail = $"AI summary failed a check: {problem}.";
                summaryChecked = false;
            }
        }
        catch (LlmException ex)
        {
            // The data is still valid, so show it rather than failing the whole request.
            answer = $"Here are the results for {result.Range}. (A written summary is unavailable right now.)";
            detail = ex.Message;
        }

        return Respond(AskOutcome.Answered, answer, detail, selection!.Report.Name, selection.Values, result, summaryChecked);
    }

    private AskResponse FromLlmFailure(LlmException ex) => ex switch
    {
        LlmLimitReachedException => Respond(AskOutcome.LimitReached, ex.Message),
        _ => Respond(AskOutcome.Unavailable, "The AI service is unavailable right now. Please try again shortly.", ex.Message),
    };

    private AskResponse Respond(
        AskOutcome outcome,
        string answer,
        string? detail = null,
        string? report = null,
        IReadOnlyDictionary<string, object>? parameters = null,
        ReportResult? result = null,
        bool? summaryChecked = null) =>
        new(outcome, answer, _llm.Name, detail, report, parameters, result, summaryChecked);

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
