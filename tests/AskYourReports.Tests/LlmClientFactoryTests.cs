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
    public void Gemini_is_never_selected_by_default_even_when_a_gemini_key_is_present()
    {
        using var app = new AppFactory { Settings = { ["GEMINI_API_KEY"] = "test-key-not-real" } };

        Assert.Equal("Fake", app.Services.GetRequiredService<LlmOptions>().Provider);
        Assert.IsType<FakeLlmClient>(app.Services.GetRequiredService<ILlmClient>());
    }

    [Theory]
    [InlineData("Anthropic", "Anthropic:ApiKey", "ANTHROPIC_API_KEY")]
    [InlineData("Gemini", "Gemini:ApiKey", "GEMINI_API_KEY")]
    public void Real_provider_without_an_api_key_fails_at_startup_with_a_clear_message(
        string provider, string secretName, string environmentVariable)
    {
        using var app = new AppFactory
        {
            Settings = { ["Llm:Provider"] = provider, [secretName] = "", [environmentVariable] = "" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<ILlmClient>());
        Assert.Contains($"Llm:Provider is '{provider}' but no API key was found", ex.Message);
        Assert.Contains(environmentVariable, ex.Message);
    }

    [Theory]
    [InlineData("Anthropic", "Anthropic:ApiKey", "anthropic")]
    [InlineData("Gemini", "Gemini:ApiKey", "gemini")]
    [InlineData("Gemini", "GEMINI_API_KEY", "gemini")]
    public void Real_provider_is_always_wrapped_in_the_cost_and_retry_guard(string provider, string keySetting, string name)
    {
        using var app = new AppFactory
        {
            Settings = { ["Llm:Provider"] = provider, [keySetting] = "test-key-not-real" },
        };

        var client = app.Services.GetRequiredService<ILlmClient>();

        Assert.IsType<GuardedLlmClient>(client);
        Assert.Equal(name, client.Name);
    }
}
