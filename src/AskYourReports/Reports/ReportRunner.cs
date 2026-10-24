using AskYourReports.Data;

namespace AskYourReports.Reports;

public sealed record ReportResult(
    string Report,
    string Range,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows)
{
    /// <summary>The rows as people see them (see <see cref="ReportFormatting"/>); sent to the page and the model.</summary>
    public IReadOnlyList<string[]> DisplayRows => ReportFormatting.FormatRows(Columns, Rows);
}

/// <summary>
/// Runs a validated report: the developer-written SQL with parameters bound, on a read-only connection.
/// </summary>
public sealed class ReportRunner
{
    private readonly SalesDatabase _database;
    private readonly TimeProvider _time;

    public ReportRunner(SalesDatabase database, TimeProvider time)
    {
        _database = database;
        _time = time;
    }

    public ReportResult Run(ReportSelection selection)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var query = selection.Report.Bind(selection.Values, today);

        using var connection = _database.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = selection.Report.Sql;
        foreach (var (name, value) in query.SqlParameters)
            command.Parameters.AddWithValue(name, value);

        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new ReportResult(selection.Report.Name, query.Range, columns, rows);
    }
}
