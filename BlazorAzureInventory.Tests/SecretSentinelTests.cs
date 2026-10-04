using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BlazorAzureInventory.Models.Configuration;
using BlazorAzureInventory.Services.AI.GraphTools;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Models.Graph;
using Microsoft.Extensions.Logging;

namespace BlazorAzureInventory.Tests;

/// <summary>
/// Critical: a sentinel placed in every secret part of every value must not appear in any snapshot, graph,
/// config entry, tool result, or log output.
/// </summary>
public class SecretSentinelTests
{
    private const string Sentinel = "S3NTINEL-DO-NOT-LEAK";

    [Fact]
    public void Sentinel_never_leaks()
    {
        var json = $$"""
            {
              "ConnectionStrings": { "Reporting": "Server=tcp:contoso-sql.database.windows.net;Database=customers;Password={{Sentinel}}" },
              "Secrets": { "ClientSecret": "{{Sentinel}}" },
              "Cosmos": "AccountEndpoint=https://contoso.documents.azure.com:443/;AccountKey={{Sentinel}};"
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var fromFile = new ConfigFlattener().FlattenJson(stream, "appsettings.Production.json", ConfigSource.AppSettingsEnvironmentJson);
        Assert.Contains(fromFile, s => s.Value!.Contains(Sentinel));

        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));

        var snapshot = Fixture.Snapshot(Sentinel, fromFile);
        var graph = new InventoryGraphBuilder(Fixture.Matcher, factory.CreateLogger<InventoryGraphBuilder>())
            .Build(snapshot, new GraphBuildOptions());
        var tools = new InventoryGraphTools(snapshot, new GraphIndex(graph), 100);

        var outputs = new List<string>
        {
            JsonSerializer.Serialize(snapshot, GraphJson.Options),
            JsonSerializer.Serialize(graph, GraphJson.Options),
            JsonSerializer.Serialize(snapshot.AppConfigs.SelectMany(a => a.Entries), GraphJson.Options),
            JsonSerializer.Serialize(tools.ListSecretLikePlainSettings(100), GraphJson.Options),
            JsonSerializer.Serialize(tools.FindConsumers("contoso-sql", 100), GraphJson.Options),
            JsonSerializer.Serialize(tools.GetAppDependencies("shop-web"), GraphJson.Options),
            new GraphQueryService().BuildContext(new GraphIndex(graph), "shop-web orders contosodata", 50),
            string.Join("\n", logs.Messages)
        };

        Assert.All(outputs, o => Assert.DoesNotContain(Sentinel, o, StringComparison.OrdinalIgnoreCase));

        // The pipeline still produced the expected identifiers.
        Assert.Contains(snapshot.AppConfigs.SelectMany(a => a.Entries), e => e.Key == "ConnectionStrings:Reporting" && e.References.Count == 1);
        Assert.Contains(graph.Edges, e => e.Type == EdgeTypes.ConnectsTo);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
