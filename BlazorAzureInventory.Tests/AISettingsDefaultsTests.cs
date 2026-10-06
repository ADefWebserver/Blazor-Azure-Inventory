using BlazorAzureInventory.Models.AI;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

public class AISettingsDefaultsTests
{
    private const string DefaultKey = "default-secret-key-1234";

    private static SessionAISettingsService Service(AIDefaultProviderOptions? defaults = null)
    {
        var host = new TagTestHost();
        var options = new AIOptions { DefaultProvider = defaults ?? new AIDefaultProviderOptions() };
        return new SessionAISettingsService(host.Store, host.UserContext, new EphemeralDataProtectionProvider(), Options.Create(options));
    }

    private static AIDefaultProviderOptions AzureDefault() => new()
    {
        ServiceType = "AzureOpenAI",
        ApiKey = DefaultKey,
        Endpoint = "https://contoso.openai.azure.com/",
        AIModel = "gpt-4o"
    };

    [Fact]
    public async Task Without_defaults_user_must_supply_a_key()
    {
        var service = Service();
        var settings = await service.GetSettingsAsync();
        var provider = settings.GetOrCreate(settings.ActiveServiceType);

        Assert.Null(service.DefaultServiceType);
        Assert.Equal(AIServiceType.OpenAI, settings.ActiveServiceType);
        Assert.False(provider.IsConfigured);
        Assert.Null(await service.GetApiKeyAsync(provider));
    }

    [Theory]
    [InlineData("", DefaultKey, "https://contoso.openai.azure.com/")]
    [InlineData("AzureOpenAI", "", "https://contoso.openai.azure.com/")]
    [InlineData("AzureOpenAI", DefaultKey, "")]
    [InlineData("NotAProvider", DefaultKey, "https://contoso.openai.azure.com/")]
    public void Incomplete_defaults_are_ignored(string type, string key, string endpoint)
    {
        var service = Service(new AIDefaultProviderOptions { ServiceType = type, ApiKey = key, Endpoint = endpoint });
        Assert.Null(service.DefaultServiceType);
    }

    [Fact]
    public async Task Defaults_prefill_settings_and_supply_the_key_without_exposing_it()
    {
        var service = Service(AzureDefault());
        var settings = await service.GetSettingsAsync();
        var provider = settings.GetOrCreate(settings.ActiveServiceType);

        Assert.Equal(AIServiceType.AzureOpenAI, settings.ActiveServiceType);
        Assert.True(provider.UsesDefaultKey);
        Assert.True(provider.IsConfigured);
        Assert.False(provider.HasStoredKey);
        Assert.Equal("", provider.ApiKey);
        Assert.Null(provider.ApiKeyLast4);
        Assert.Equal("https://contoso.openai.azure.com/", provider.Endpoint);
        Assert.Equal("gpt-4o", provider.AIModel);
        Assert.Equal(DefaultKey, await service.GetApiKeyAsync(provider));

        Assert.False(settings.GetOrCreate(AIServiceType.OpenAI).IsConfigured);
    }

    [Theory]
    [InlineData("https://evil.openai.azure.com/")]
    [InlineData("http://contoso.openai.azure.com/")]
    [InlineData("https://contoso.openai.azure.com.evil.com/")]
    [InlineData("")]
    public async Task Default_key_is_not_sent_to_a_different_endpoint(string endpoint)
    {
        var service = Service(AzureDefault());
        var provider = (await service.GetSettingsAsync()).GetOrCreate(AIServiceType.AzureOpenAI);
        provider.Endpoint = endpoint;

        Assert.False(service.CanUseDefaultKey(provider));
        Assert.Null(await service.GetApiKeyAsync(provider));
    }

    [Fact]
    public async Task Default_key_follows_the_same_host_with_a_different_path()
    {
        var service = Service(AzureDefault());
        var provider = (await service.GetSettingsAsync()).GetOrCreate(AIServiceType.AzureOpenAI);
        provider.Endpoint = "https://CONTOSO.openai.azure.com";

        Assert.Equal(DefaultKey, await service.GetApiKeyAsync(provider));
    }

    [Fact]
    public async Task Default_key_is_not_used_for_other_provider_types()
    {
        var service = Service(new AIDefaultProviderOptions { ServiceType = "OpenAI", ApiKey = DefaultKey, AIModel = "gpt-4o" });
        var settings = await service.GetSettingsAsync();

        Assert.Equal(DefaultKey, await service.GetApiKeyAsync(settings.GetOrCreate(AIServiceType.OpenAI)));
        Assert.Null(await service.GetApiKeyAsync(settings.GetOrCreate(AIServiceType.Anthropic)));
    }

    [Fact]
    public async Task User_key_overrides_the_default_and_clearing_restores_it()
    {
        var service = Service(AzureDefault());
        var settings = await service.GetSettingsAsync();
        var provider = settings.GetOrCreate(AIServiceType.AzureOpenAI);
        provider.ApiKey = "my-own-key-9876";
        provider.AIModel = "my-deployment";
        await service.SaveSettingsAsync(settings);

        var saved = (await service.GetSettingsAsync()).GetOrCreate(AIServiceType.AzureOpenAI);
        Assert.True(saved.HasStoredKey);
        Assert.False(saved.UsesDefaultKey);
        Assert.Equal("9876", saved.ApiKeyLast4);
        Assert.Equal("my-deployment", saved.AIModel);
        Assert.Equal("my-own-key-9876", await service.GetApiKeyAsync(saved));

        await service.ClearAsync();
        var cleared = (await service.GetSettingsAsync()).GetOrCreate(AIServiceType.AzureOpenAI);
        Assert.True(cleared.UsesDefaultKey);
        Assert.Equal("gpt-4o", cleared.AIModel);
        Assert.Equal(DefaultKey, await service.GetApiKeyAsync(cleared));
    }

    [Fact]
    public async Task Saving_without_a_key_keeps_using_the_default()
    {
        var service = Service(AzureDefault());
        var settings = await service.GetSettingsAsync();
        settings.GetOrCreate(AIServiceType.AzureOpenAI).AIModel = "other-deployment";
        await service.SaveSettingsAsync(settings);

        var provider = (await service.GetSettingsAsync()).GetOrCreate(AIServiceType.AzureOpenAI);
        Assert.True(provider.UsesDefaultKey);
        Assert.Equal("other-deployment", provider.AIModel);
        Assert.Equal(DefaultKey, await service.GetApiKeyAsync(provider));
    }

    [Theory]
    [InlineData("https://evil.example.com/openai/deployments/x")]
    [InlineData("http://evil.example.com/openai/deployments/x")]
    public async Task Absolute_deployment_path_cannot_leave_the_endpoint_host(string deploymentPath)
    {
        var options = Options.Create(new AIOptions());
        var guard = new EndpointGuard(options, (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("20.1.2.3") }));
        var factory = new ChatClientFactory(Service(), guard, new NoHttpClientFactory(), options);
        var provider = new AIProviderSettings
        {
            ServiceType = AIServiceType.AzureOpenAI,
            Endpoint = "https://contoso.openai.azure.com/",
            DeploymentPath = deploymentPath,
            AIModel = "gpt-4o"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateForAsync(provider, DefaultKey, default));
        Assert.Contains("relative", ex.Message);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
