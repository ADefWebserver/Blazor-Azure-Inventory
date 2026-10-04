using System.Text;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Services.Analysis;

namespace BlazorAzureInventory.Tests;

public class SecretClassifierTests
{
    private readonly SecretClassifier _secrets = new();

    [Theory]
    [InlineData("Db:Password", "hunter2")]
    [InlineData("ApiKey", "abc")]
    [InlineData("Storage", "DefaultEndpointsProtocol=https;AccountName=a;AccountKey=xyz")]
    [InlineData("Bus", "Endpoint=sb://n.servicebus.windows.net/;SharedAccessKey=b")]
    [InlineData("Token", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    [InlineData("Blob", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWZnaGlqa2w=")]
    public void Detects_secret_like_values(string key, string value) => Assert.True(_secrets.IsSecretLike(key, value));

    [Theory]
    [InlineData("Logging:LogLevel:Default", "Information")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("ApiKey", "@Microsoft.KeyVault(VaultName=contoso-kv;SecretName=api-key)")]
    [InlineData("Password", "")]
    public void Ignores_plain_values_and_key_vault_references(string key, string value) =>
        Assert.False(_secrets.IsSecretLike(key, value));
}

public class ConfigFlattenerTests
{
    private readonly ConfigFlattener _flattener = new();

    [Fact]
    public void Flattens_nested_objects_and_arrays_with_comments()
    {
        const string json = """
            {
              // comment
              "ConnectionStrings": { "Default": "Server=x" },
              "Storage": { "Blob": { "AccountName": "contosodata" } },
              "Hosts": [ "a", "b" ],
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var keys = _flattener.FlattenJson(stream, "appsettings.json", ConfigSource.AppSettingsJson).Select(s => s.Key).ToList();

        Assert.Contains("ConnectionStrings:Default", keys);
        Assert.Contains("Storage:Blob:AccountName", keys);
        Assert.Contains("Hosts:0", keys);
        Assert.Contains("Hosts:1", keys);
    }

    [Fact]
    public void Normalizes_environment_and_connection_string_keys()
    {
        Assert.Equal("ConnectionStrings:Orders", ConfigFlattener.NormalizeEnvironmentKey("ConnectionStrings__Orders"));
        Assert.Equal("ConnectionStrings:Orders", ConfigFlattener.ConnectionStringKey("Orders"));
    }

    [Fact]
    public void App_service_connection_string_overrides_json_entry()
    {
        RawSetting[] settings =
        [
            new("ConnectionStrings:Default", "a", ConfigSource.AppSettingsJson, "appsettings.json"),
            new("connectionstrings:default", "b", ConfigSource.AppServiceConnectionString, null),
            new("Feature", "1", ConfigSource.AppSettingsJson, "appsettings.json"),
            new("Feature", "2", ConfigSource.AppSettingsEnvironmentJson, "appsettings.Production.json"),
            new("Feature", "3", ConfigSource.AppServiceAppSetting, null),
        ];

        var result = _flattener.ApplyPrecedence(settings);

        Assert.Equal(5, result.Count);
        Assert.True(result[0].IsOverridden);
        Assert.False(result[1].IsOverridden);
        Assert.True(result[2].IsOverridden);
        Assert.True(result[3].IsOverridden);
        Assert.False(result[4].IsOverridden);
    }

    [Fact]
    public void Container_hint_is_dropped_without_a_storage_reference()
    {
        var entries = Fixture.Analyzer.Analyze("app", null, null,
            [new RawSetting("Images:Container", "product-images", ConfigSource.AppServiceAppSetting, null)]);
        Assert.Empty(entries.Single().References);
    }

    [Fact]
    public void Container_hint_uses_the_apps_storage_account()
    {
        var entries = Fixture.Analyzer.Analyze("app", null, null,
        [
            new RawSetting("Storage", "AccountName=contosodata;AccountKey=x", ConfigSource.AppServiceAppSetting, null),
            new RawSetting("Images:Container", "product-images", ConfigSource.AppServiceAppSetting, null),
        ]);
        var hint = entries.Single(e => e.Key == "Images:Container").References.Single();
        Assert.Equal("contosodata", hint.AccountOrServerName);
    }
}
