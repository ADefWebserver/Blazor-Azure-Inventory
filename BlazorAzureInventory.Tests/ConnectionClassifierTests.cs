using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Services.Analysis;

namespace BlazorAzureInventory.Tests;

public class ConnectionClassifierTests
{
    private readonly ConnectionClassifier _classifier = Fixture.Classifier;

    private ConnectionReference Single(string key, string value)
    {
        var refs = _classifier.Classify(key, value, ConfigSource.AppServiceAppSetting);
        Assert.Single(refs);
        return refs[0];
    }

    [Fact]
    public void Sql_connection_string_with_tcp_prefix_and_port()
    {
        var r = Single("ConnectionStrings:Orders",
            "Server=tcp:contoso-sql.database.windows.net,1433;Initial Catalog=orders;User ID=x;Password=y;");
        Assert.Equal(ConnectionTargetKind.SqlDatabase, r.Kind);
        Assert.Equal("contoso-sql.database.windows.net", r.Host);
        Assert.Equal("orders", r.DatabaseName);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Non_azure_sql_host_is_medium_confidence()
    {
        var r = Single("ConnectionStrings:Legacy", "Data Source=10.0.0.5;Initial Catalog=hr;Integrated Security=true");
        Assert.Equal(ConnectionTargetKind.SqlDatabase, r.Kind);
        Assert.Equal("10.0.0.5", r.Host);
        Assert.Equal("hr", r.DatabaseName);
        Assert.Equal(Confidence.Medium, r.Confidence);
    }

    [Fact]
    public void Storage_connection_string()
    {
        var r = Single("Storage", "DefaultEndpointsProtocol=https;AccountName=contosodata;AccountKey=abc==;EndpointSuffix=core.windows.net");
        Assert.Equal(ConnectionTargetKind.StorageAccount, r.Kind);
        Assert.Equal("contosodata", r.AccountOrServerName);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Blob_container_uri()
    {
        var r = Single("Uploads:Uri", "https://contosodata.blob.core.windows.net/uploads");
        Assert.Equal(ConnectionTargetKind.BlobContainer, r.Kind);
        Assert.Equal("contosodata", r.AccountOrServerName);
        Assert.Equal("uploads", r.ContainerName);
    }

    [Theory]
    [InlineData("AzureWebJobsStorage__accountName")]
    [InlineData("AzureWebJobsStorage:accountName")]
    public void Identity_based_storage_account(string key)
    {
        var r = Single(key, "funcstore01");
        Assert.Equal(ConnectionTargetKind.StorageAccount, r.Kind);
        Assert.Equal("funcstore01", r.AccountOrServerName);
        Assert.True(ConnectionClassifier.IsIdentityBasedKey(key));
    }

    [Fact]
    public void Redis_host_and_port()
    {
        var r = Single("Redis", "contoso.redis.cache.windows.net:6380,password=x,ssl=True");
        Assert.Equal(ConnectionTargetKind.RedisCache, r.Kind);
        Assert.Equal("contoso.redis.cache.windows.net", r.Host);
    }

    [Fact]
    public void Cosmos_account_endpoint()
    {
        var r = Single("Cosmos", "AccountEndpoint=https://contoso.documents.azure.com:443/;AccountKey=x;");
        Assert.Equal(ConnectionTargetKind.CosmosAccount, r.Kind);
        Assert.Equal("contoso.documents.azure.com", r.Host);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Service_bus_endpoint()
    {
        var r = Single("Bus", "Endpoint=sb://contoso-bus.servicebus.windows.net/;SharedAccessKeyName=a;SharedAccessKey=b");
        Assert.Equal(ConnectionTargetKind.ServiceBusNamespace, r.Kind);
        Assert.Equal("contoso-bus.servicebus.windows.net", r.Host);
    }

    [Theory]
    [InlineData("@Microsoft.KeyVault(VaultName=contoso-kv;SecretName=api-key)")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://contoso-kv.vault.azure.net/secrets/api-key/0123456789)")]
    public void Key_vault_reference(string value)
    {
        var r = Single("ApiKey", value);
        Assert.Equal(ConnectionTargetKind.KeyVault, r.Kind);
        Assert.Equal("contoso-kv", r.AccountOrServerName);
        Assert.Equal("api-key", r.SecretName);
    }

    [Fact]
    public void Container_name_hint_is_low_confidence()
    {
        var r = Single("Images:Container", "product-images");
        Assert.Equal(ConnectionTargetKind.BlobContainer, r.Kind);
        Assert.Equal("product-images", r.ContainerName);
        Assert.Equal(Confidence.Low, r.Confidence);
    }

    [Fact]
    public void Plain_setting_has_no_reference()
    {
        Assert.Empty(_classifier.Classify("Logging:LogLevel:Default", "Information", ConfigSource.AppSettingsJson));
    }

    [Fact]
    public void Storage_emulator_is_flagged()
    {
        var r = Single("Storage", "UseDevelopmentStorage=true");
        Assert.True(r.IsLocalEmulator);
    }

    [Fact]
    public void Sovereign_cloud_sql_suffix_is_high_confidence()
    {
        var r = Single("Db", "Server=tcp:gov-sql.database.usgovcloudapi.net,1433;Database=orders;");
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Configured_suffixes_replace_defaults()
    {
        var classifier = new ConnectionClassifier(new CloudEndpointSuffixes(new CloudEndpointSuffixOptions { Sql = [".database.windows.net"] }));
        var r = classifier.Classify("Db", "Server=gov-sql.database.usgovcloudapi.net;Database=orders;", ConfigSource.AppServiceAppSetting).Single();
        Assert.Equal(Confidence.Medium, r.Confidence);
    }

    [Theory]
    [InlineData("  Server = TCP:Contoso-SQL.database.windows.net , 1433 ; Database = orders ")]
    [InlineData("server=contoso-sql.database.windows.net.;database=orders")]
    public void Whitespace_casing_and_trailing_dots_are_normalized(string value)
    {
        var r = Single("Db", value);
        Assert.Equal("contoso-sql.database.windows.net", r.Host);
    }

    [Theory]
    [InlineData("not a connection string")]
    [InlineData("=;=;")]
    [InlineData("https://example.com/path")]
    public void Malformed_or_unrelated_values_produce_no_reference(string value)
    {
        Assert.Empty(_classifier.Classify("Something", value, ConfigSource.AppServiceAppSetting));
    }

    [Theory]
    [InlineData("contosoacr.azurecr.io/catalog-api:1.4.2", "contosoacr.azurecr.io", "catalog-api")]
    [InlineData("DOCKER|contosoacr.azurecr.io/team/web@sha256:abc", "contosoacr.azurecr.io", "team/web")]
    public void Container_images_are_parsed_without_tags(string image, string registry, string repo)
    {
        var parsed = ConnectionClassifier.ParseImage(image);
        Assert.NotNull(parsed);
        Assert.Equal(registry, parsed.RegistryHost);
        Assert.Equal(repo, parsed.Repository);
    }

    [Theory]
    [InlineData("nginx:latest")]
    [InlineData("DOTNETCORE|8.0")]
    public void Non_registry_images_are_ignored(string image) => Assert.Null(ConnectionClassifier.ParseImage(image));
}
