using System.Security.Claims;
using BlazorAzureInventory.Models.Graph;
using BlazorAzureInventory.Models.Inventory;
using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Changes;
using BlazorAzureInventory.Services.Graph;
using BlazorAzureInventory.Services.Quotas;
using BlazorAzureInventory.Services.Reports;
using ClosedXML.Excel;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

public class QuotaTests
{
    private static ClaimsPrincipal User(string oid, string? role = AppRoles.User) => new(new ClaimsIdentity(
    [
        new Claim("http://schemas.microsoft.com/identity/claims/tenantid", Fixture.TenantId),
        new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", oid),
        .. role is null ? Array.Empty<Claim>() : [new Claim(AppClaimTypes.Role, role)]
    ], "test"));

    [Fact]
    public void Limits_scans_per_hour_and_concurrency()
    {
        var quotas = new UserQuotaService(Options.Create(new QuotaOptions { Scans = { PerUserPerHour = 2, MaxConcurrentGlobal = 5 } }), TimeProvider.System);
        var alice = User(Guid.NewGuid().ToString());

        using (var first = quotas.TryStartScan(alice, out _))
        {
            Assert.NotNull(first);
            Assert.Null(quotas.TryStartScan(alice, out var concurrent));
            Assert.Contains("already running", concurrent.Message);
        }
        using (quotas.TryStartScan(alice, out _)) { }
        Assert.Null(quotas.TryStartScan(alice, out var hourly));
        Assert.Contains("Scan limit", hourly.Message);
        using var other = quotas.TryStartScan(User(Guid.NewGuid().ToString()), out _);
        Assert.NotNull(other);
    }

    [Fact]
    public void Admins_are_exempt_only_when_configured()
    {
        var options = new QuotaOptions { AdminsExempt = true, Reports = { PerUserPerHour = 1 } };
        var quotas = new UserQuotaService(Options.Create(options), TimeProvider.System);
        var admin = User(Guid.NewGuid().ToString(), AppRoles.Admin);
        var user = User(Guid.NewGuid().ToString());

        Assert.True(quotas.TryReport(admin).Allowed);
        Assert.True(quotas.TryReport(admin).Allowed);
        Assert.True(quotas.TryReport(user).Allowed);
        Assert.False(quotas.TryReport(user).Allowed);
    }

    [Fact]
    public void Default_provider_token_allowance_is_enforced()
    {
        var quotas = new UserQuotaService(Options.Create(new QuotaOptions { Chat = { DefaultProviderTokensPerUserPerDay = 1000 } }), TimeProvider.System);
        var user = User(Guid.NewGuid().ToString());

        Assert.True(quotas.TryChatTurn(user, usesDefaultKey: true).Allowed);
        quotas.RecordDefaultProviderTokens(user, 1500);
        Assert.False(quotas.TryChatTurn(user, usesDefaultKey: true).Allowed);
        Assert.True(quotas.TryChatTurn(user, usesDefaultKey: false).Allowed);
    }
}

public class ReportTests
{
    private static ReportExportService Service() => new(Options.Create(new QuotaOptions()));

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "'=HYPERLINK(\"x\")")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("normal", "normal")]
    public void Sanitizes_formula_like_values(string input, string expected) => Assert.Equal(expected, ReportExportService.Sanitize(input));

    [Fact]
    public void Csv_escapes_and_neutralizes_cells()
    {
        var table = new ReportTable("T", ["A", "B"], [["=1+1", "a,\"b\""]]);
        using var stream = Service().BuildCsv(table);
        var text = new StreamReader(stream).ReadToEnd();

        Assert.Contains("'=1+1,\"a,\"\"b\"\"\"", text);
    }

    [Fact]
    public void Workbook_has_expected_sheets_and_no_secrets()
    {
        const string secret = "Workbook-Secret-Value";
        var snapshot = Fixture.Snapshot(secret);
        var graph = Fixture.Builder().Build(snapshot, new GraphBuildOptions());
        using var stream = Service().BuildInventoryWorkbook(snapshot, new GraphIndex(graph), null, "tester@contoso.com");
        using var workbook = new XLWorkbook(stream);

        var names = workbook.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal("Summary", names[0]);
        Assert.Contains("Web & Function Apps", names);
        Assert.Contains("Connections", names);
        Assert.Contains("Scan Issues", names);
        Assert.DoesNotContain(workbook.Worksheets.SelectMany(w => w.CellsUsed()), c => c.GetString().Contains(secret));
        Assert.Contains(workbook.Worksheet("Connections").CellsUsed(), c => c.GetString() == "ConnectionStrings:Orders");
    }
}

public class SnapshotDiffTests
{
    private static SnapshotDiffService Service() =>
        new(Fixture.Builder(), Options.Create(new ChangeTrackingOptions()), Options.Create(new InventoryOptions()));

    [Fact]
    public void Identical_scans_have_no_changes()
    {
        var snapshot = Fixture.Snapshot();
        Assert.Equal(0, Service().Compare(snapshot, snapshot).TotalCount);
    }

    [Fact]
    public void Detects_added_removed_changed_and_connection_changes()
    {
        var previous = Fixture.Snapshot();
        var removedId = Fixture.ContosoBus;
        var newDb = Fixture.Resource(Fixture.ContosoSql + "/databases/audit", AzureResourceKind.SqlDatabase, Fixture.ContosoSql);
        var current = previous with
        {
            ScannedUtc = previous.ScannedUtc.AddDays(1),
            Resources = previous.Resources
                .Where(r => r.Id != removedId)
                .Select(r => r.Id == Fixture.OrdersDb
                    ? r with { Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["owner"] = "data-team" } }
                    : r)
                .Append(newDb)
                .ToList(),
            AppConfigs = previous.AppConfigs.Where(a => a.AppId != Fixture.CatalogApi).ToList()
        };

        var diff = Service().Compare(previous, current);

        Assert.Equal([newDb.Id], diff.Added.Select(r => r.Id));
        Assert.Equal([removedId], diff.Removed.Select(r => r.Id));
        var changed = Assert.Single(diff.Changed);
        Assert.Equal(Fixture.OrdersDb, changed.Id);
        Assert.Contains("Tag added: owner = data-team", changed.Changes);
        Assert.Contains(diff.ConnectionsRemoved, c => c.SourceId == Fixture.CatalogApi && c.Type == EdgeTypes.PullsImageFrom);
        Assert.Contains(Fixture.CatalogApi, diff.ChangedIds);
    }

    [Fact]
    public void Ignores_subscriptions_not_in_both_scans()
    {
        var previous = Fixture.Snapshot();
        var current = previous with { Subscriptions = [], Resources = [], AppConfigs = [] };

        Assert.Equal(0, Service().Compare(previous, current).TotalCount);
    }
}
