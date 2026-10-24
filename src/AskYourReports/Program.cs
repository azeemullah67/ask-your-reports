using AskYourReports;
using AskYourReports.Data;
using AskYourReports.Llm;
using AskYourReports.Reports;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SalesDatabase(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Sales") ?? "Data Source=reports.db"));
builder.Services.AddSingleton<ReportRunner>();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions());
builder.Services.AddSingleton(LlmClientFactory.Create);
builder.Services.AddScoped<AskService>();

var app = builder.Build();

var today = DateOnly.FromDateTime(app.Services.GetRequiredService<TimeProvider>().GetLocalNow().DateTime);
var seed = app.Services.GetRequiredService<SalesDatabase>().EnsureSeeded(today);
app.Logger.LogInformation("Database ready: {Products} products, {Sales} sales (newly seeded: {Seeded}). LLM provider: {Provider}",
    seed.Products, seed.Sales, seed.Seeded, app.Services.GetRequiredService<ILlmClient>().Name);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/ask", async (AskRequest request, AskService service, CancellationToken cancellationToken) =>
{
    var response = await service.AskAsync(request.Question, cancellationToken);
    var status = response.Outcome switch
    {
        AskOutcome.Answered => StatusCodes.Status200OK,
        AskOutcome.InvalidQuestion => StatusCodes.Status400BadRequest,
        AskOutcome.CouldNotMap => StatusCodes.Status422UnprocessableEntity,
        AskOutcome.LimitReached => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status503ServiceUnavailable,
    };
    return Results.Json(response, statusCode: status);
});

app.Run();

public sealed record AskRequest(string? Question);

// Exposed so tests can reference the app assembly.
public partial class Program;
