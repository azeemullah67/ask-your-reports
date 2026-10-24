using AskYourReports.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace AskYourReports.Tests;

/// <summary>Which client the app wires up from config. None of these make network calls.</summary>
public class LlmClientFactoryTests
{
    [Fact]
    public void Default_configuration_uses_the_fake_client()
    {
        using var app = new AppFactory();

        Assert.IsType<FakeLlmClient>(app.Services.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void Real_provider_without_an_api_key_fails_at_startup_with_a_clear_message()
    {
        using var app = new AppFactory
        {
            Settings = { ["Llm:Provider"] = "Anthropic", ["Anthropic:ApiKey"] = "", ["ANTHROPIC_API_KEY"] = "" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<ILlmClient>());
        Assert.Contains("no API key was found", ex.Message);
    }

    [Fact]
    public void Real_provider_is_always_wrapped_in_the_cost_and_retry_guard()
    {
        using var app = new AppFactory
        {
            Settings = { ["Llm:Provider"] = "Anthropic", ["Anthropic:ApiKey"] = "test-key-not-real" },
        };

        var client = app.Services.GetRequiredService<ILlmClient>();

        Assert.IsType<GuardedLlmClient>(client);
        Assert.Equal("anthropic", client.Name);
    }
}
