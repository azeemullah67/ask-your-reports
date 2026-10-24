using AskYourReports.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Sales") ?? "Data Source=reports.db";
builder.Services.AddSingleton(new SalesDatabase(connectionString));

var app = builder.Build();

var seed = app.Services.GetRequiredService<SalesDatabase>().EnsureSeeded(DateOnly.FromDateTime(DateTime.Today));
app.Logger.LogInformation("Database ready: {Products} products, {Sales} sales (newly seeded: {Seeded})",
    seed.Products, seed.Sales, seed.Seeded);

app.MapGet("/", () => "Hello World!");

app.Run();

// Exposed so tests can reference the app assembly.
public partial class Program;
