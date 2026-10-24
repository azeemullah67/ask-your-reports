using System.Text.Json;

namespace AskYourReports.Reports;

/// <summary>
/// A report choice that has passed validation: the report exists and every parameter is typed and in range.
/// </summary>
public sealed record ReportSelection(ReportDefinition Report, IReadOnlyDictionary<string, object> Values);

/// <summary>
/// Checks the LLM's JSON before anything touches the database.
/// Expected shape: {"report": "worst_products", "parameters": {"period": "last_month", "limit": 5}}
/// Anything else (bad JSON, unknown report, unknown or out-of-range parameter) is rejected.
/// </summary>
public static class ReportSelectionValidator
{
    private const int MaxJsonLength = 2_000;

    public static bool TryValidate(string? json, out ReportSelection? selection, out string error)
    {
        selection = null;
        error = "";

        if (string.IsNullOrWhiteSpace(json))
            return Fail("the model returned an empty response", out error);
        if (json.Length > MaxJsonLength)
            return Fail("the model response was too long", out error);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Fail("the model response was not valid JSON", out error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("the model response was not a JSON object", out error);

            JsonElement reportElement = default;
            JsonElement parametersElement = default;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "report" when reportElement.ValueKind == JsonValueKind.Undefined:
                        reportElement = property.Value;
                        break;
                    case "parameters" when parametersElement.ValueKind == JsonValueKind.Undefined:
                        parametersElement = property.Value;
                        break;
                    default:
                        return Fail($"unexpected or duplicate field '{property.Name}'", out error);
                }
            }

            if (reportElement.ValueKind != JsonValueKind.String)
                return Fail("the 'report' field is missing or not a string", out error);

            var reportName = reportElement.GetString()!;
            var report = ReportCatalog.Find(reportName);
            if (report is null)
                return Fail($"unknown report '{reportName}'", out error);

            if (!TryReadParameters(report, parametersElement, out var values, out error))
                return false;

            selection = new ReportSelection(report, values);
            return true;
        }
    }

    private static bool TryReadParameters(
        ReportDefinition report, JsonElement parameters, out Dictionary<string, object> values, out string error)
    {
        values = new Dictionary<string, object>();
        error = "";

        if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            return Fail("the 'parameters' field is not an object", out error);

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parameters.EnumerateObject())
            {
                var spec = report.Parameters.FirstOrDefault(p => p.Name == property.Name);
                if (spec is null)
                    return Fail($"report '{report.Name}' has no parameter '{property.Name}'", out error);
                if (values.ContainsKey(spec.Name))
                    return Fail($"parameter '{spec.Name}' appears more than once", out error);
                if (!TryReadValue(spec, property.Value, out var value, out error))
                    return false;
                values[spec.Name] = value;
            }
        }

        foreach (var spec in report.Parameters)
            values.TryAdd(spec.Name, spec.Default);

        return true;
    }

    private static bool TryReadValue(ParameterSpec spec, JsonElement element, out object value, out string error)
    {
        value = spec.Default;
        error = "";

        switch (spec.Kind)
        {
            case ParameterKind.Integer:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
                    return Fail($"parameter '{spec.Name}' must be a whole number", out error);
                if (number < spec.Min || number > spec.Max)
                    return Fail($"parameter '{spec.Name}' must be between {spec.Min} and {spec.Max}", out error);
                value = number;
                return true;

            case ParameterKind.Choice:
                var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (text is null || !spec.Choices!.Contains(text, StringComparer.Ordinal))
                    return Fail($"parameter '{spec.Name}' must be one of: {string.Join(", ", spec.Choices!)}", out error);
                value = text;
                return true;

            default:
                return Fail($"parameter '{spec.Name}' has an unsupported type", out error);
        }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
