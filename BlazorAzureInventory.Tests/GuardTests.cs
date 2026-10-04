using System.Net;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.AI;
using BlazorAzureInventory.Services.Azure;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

public class ScmHostValidatorTests
{
    private readonly ScmHostValidator _validator = new(Options.Create(new InventoryOptions()));

    [Theory]
    [InlineData("shop-web.scm.azurewebsites.net")]
    [InlineData("shop-web-abc123.scm.eastus-01.azurewebsites.net")]
    [InlineData("app.scm.my-ase.appserviceenvironment.net")]
    public void Accepts_valid_scm_hosts(string host)
    {
        Assert.True(_validator.TryCreateBaseUri(host, out var uri));
        Assert.Equal("https", uri.Scheme);
    }

    [Theory]
    [InlineData("evil-azurewebsites.net")]
    [InlineData("scm.evil-azurewebsites.net")]
    [InlineData("shop-web.scm.azurewebsites.net.evil.com")]
    [InlineData("shop-web.azurewebsites.net")]
    [InlineData("10.0.0.1")]
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData("user@shop-web.scm.azurewebsites.net")]
    public void Rejects_other_hosts(string host) => Assert.False(_validator.TryCreateBaseUri(host, out _));

    [Fact]
    public void Rejects_http_uris() => Assert.False(_validator.IsAllowed(new Uri("http://shop-web.scm.azurewebsites.net/")));
}

public class EndpointGuardTests
{
    private static EndpointGuard Guard(params IPAddress[] addresses) =>
        new(Options.Create(new AIOptions()), (_, _) => Task.FromResult(addresses));

    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/api/projects/p1")]
    [InlineData("https://contoso.cognitiveservices.azure.com")]
    public async Task Accepts_allowlisted_public_hosts(string endpoint)
    {
        Assert.Null(await Guard(IPAddress.Parse("20.1.2.3")).ValidateAsync(endpoint));
    }

    [Theory]
    [InlineData("http://contoso.openai.azure.com/")]
    [InlineData("https://localhost/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://contoso.openai.azure.com.evil.com/")]
    [InlineData("https://evil.com/")]
    [InlineData("https://user:pw@contoso.openai.azure.com/")]
    [InlineData("https://contoso.openai.azure.com:8443/")]
    public async Task Rejects_bad_formats(string endpoint)
    {
        Assert.NotNull(await Guard(IPAddress.Parse("20.1.2.3")).ValidateAsync(endpoint));
    }

    [Theory]
    [InlineData("10.0.0.4")]
    [InlineData("172.16.5.5")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public async Task Rejects_hosts_resolving_to_private_addresses(string ip)
    {
        Assert.NotNull(await Guard(IPAddress.Parse(ip)).ValidateAsync("https://contoso.openai.azure.com/"));
    }
}
