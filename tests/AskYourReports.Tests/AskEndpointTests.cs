using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AskYourReports.Tests;

/// <summary>Runs the real app in memory with a temporary database. Uses the default (fake) LLM client unless settings say otherwise.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ayr-e2e-{Guid.NewGuid():N}.db");

    public Dictionary<string, string> Settings { get; init; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Sales", $"Data Source={_dbPath};Pooling=False");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedTimeProvider()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_dbPath);
    }
}

public class AskEndpointTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Post_ask_answers_a_question_end_to_end()
    {
        var response = await _client.PostAsJsonAsync("/ask", new { question = "Which products earned the least revenue last month?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("Answered", body.GetProperty("outcome").GetString());
        Assert.Equal("worst_products", body.GetProperty("report").GetString());
        Assert.Equal("fake", body.GetProperty("provider").GetString());
        Assert.Equal(5, body.GetProperty("result").GetProperty("rows").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("answer").GetString()));
    }

    [Fact]
    public async Task Post_ask_returns_a_clear_message_when_the_question_cannot_be_mapped()
    {
        var response = await _client.PostAsJsonAsync("/ask", new { question = "What's the weather tomorrow?" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("CouldNotMap", body.GetProperty("outcome").GetString());
        Assert.StartsWith("I couldn't map that question", body.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Post_ask_rejects_an_empty_question()
    {
        var response = await _client.PostAsJsonAsync("/ask", new { question = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Root_serves_the_html_page()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("<form id=\"ask-form\">", html);
        Assert.Contains("Which products earned the least revenue last month?", html);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;
}
