using System.Text.Json;
using Azure;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.Analysis;
using BlazorAzureInventory.Services.Azure;
using BlazorAzureInventory.Services.Tags;

namespace BlazorAzureInventory.Tests;

public class TagValidatorTests
{
    private readonly TagValidator _validator = new(new SecretClassifier());

    [Theory]
    [InlineData("env", true)]
    [InlineData("", false)]
    [InlineData("a<b", false)]
    [InlineData("a/b", false)]
    [InlineData("a?b", false)]
    [InlineData("100%", false)]
    [InlineData("microsoft-owner", false)]
    [InlineData("Azure.team", false)]
    [InlineData("windowsVersion", false)]
    [InlineData("conn-out-SqlDatabase", true)]
    public void Validates_names(string name, bool valid) =>
        Assert.Equal(valid, _validator.ValidateName(name, "microsoft.web/sites").IsValid);

    [Fact]
    public void Storage_accounts_limit_names_to_128_characters()
    {
        var name = new string('a', 129);
        Assert.True(_validator.ValidateName(name, "microsoft.web/sites").IsValid);
        Assert.False(_validator.ValidateName(name, "microsoft.storage/storageaccounts").IsValid);
        Assert.False(_validator.ValidateName(new string('a', 513), "microsoft.web/sites").IsValid);
    }

    [Fact]
    public void Values_are_limited_to_256_characters()
    {
        Assert.True(_validator.ValidateValue("x", new string('v', 256)).IsValid);
        Assert.False(_validator.ValidateValue("x", new string('v', 257)).IsValid);
    }

    [Fact]
    public void Sets_are_limited_to_50_tags_and_reject_case_insensitive_duplicates()
    {
        var fifty = Enumerable.Range(0, 50).Select(i => new KeyValuePair<string, string>($"t{i}", "v")).ToList();
        Assert.True(_validator.ValidateSet(fifty, null).IsValid);
        Assert.False(_validator.ValidateSet([.. fifty, new("t50", "v")], null).IsValid);
        Assert.False(_validator.ValidateSet([new("Env", "a"), new("env", "b")], null).IsValid);
    }

    [Fact]
    public void Warns_but_does_not_block_hidden_prefix_whitespace_and_secret_like_values()
    {
        var result = _validator.ValidateSet([new("hidden-x", "v"), new(" padded ", "v"), new("note", "AccountKey=abc")], null);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void Existing_tags_skip_name_rules_but_new_ones_do_not()
    {
        var existing = new Dictionary<string, string> { ["hidden-link: /x"] = "y" };
        Assert.True(_validator.ValidateSet([new("hidden-link: /x", "y"), new("ok", "v")], null, existing).IsValid);
        Assert.False(_validator.ValidateSet([new("hidden-link: /x", "y"), new("a/b", "v")], null, existing).IsValid);
        Assert.False(_validator.ValidateSet([new("hidden-link: /x", "y")], null).IsValid);
    }

    [Fact]
    public void Blob_containers_are_not_taggable() =>
        Assert.False(_validator.ValidateSet([new("a", "b")], "microsoft.storage/storageaccounts/blobservices/containers").IsValid);
}

public class TagDiffBuilderTests
{
    private static readonly Dictionary<string, string> Baseline = TagTestHost.Tags(("env", "prod"), ("owner", "team-a"), ("cost", "1"));

    [Fact]
    public void Detects_add_update_remove_and_no_op()
    {
        var rows = TagDiffBuilder.ToRows(Baseline);
        rows.Single(r => r.Name == "owner").Value = "team-b";
        rows.Single(r => r.Name == "cost").IsRemoved = true;
        rows.Add(new TagRow { Name = "app", Value = "shop" });

        var changes = TagDiffBuilder.Build(Baseline, rows);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c is { Name: "app", Kind: TagChangeKind.Add, NewValue: "shop" });
        Assert.Contains(changes, c => c is { Name: "owner", Kind: TagChangeKind.Update, OldValue: "team-a", NewValue: "team-b" });
        Assert.Contains(changes, c => c is { Name: "cost", Kind: TagChangeKind.Remove, OldValue: "1" });
        Assert.Empty(TagDiffBuilder.Build(Baseline, TagDiffBuilder.ToRows(Baseline)));
    }

    [Fact]
    public void Rename_is_remove_plus_add_and_case_only_rename_is_one_update()
    {
        var rows = TagDiffBuilder.ToRows(Baseline);
        rows.Single(r => r.Name == "env").Name = "environment";
        var changes = TagDiffBuilder.Build(Baseline, rows);
        Assert.Contains(changes, c => c is { Name: "env", Kind: TagChangeKind.Remove });
        Assert.Contains(changes, c => c is { Name: "environment", Kind: TagChangeKind.Add, NewValue: "prod" });

        rows = TagDiffBuilder.ToRows(Baseline);
        rows.Single(r => r.Name == "env").Name = "Env";
        var caseOnly = Assert.Single(TagDiffBuilder.Build(Baseline, rows));
        Assert.Equal(TagChangeKind.Update, caseOnly.Kind);
        Assert.Equal("Env", caseOnly.Name);
        Assert.Equal("Env", Assert.Single(TagDiffBuilder.Apply(Baseline, [caseOnly]), kv => kv.Key.Equals("env", StringComparison.OrdinalIgnoreCase)).Key);
    }

    [Fact]
    public void Apply_keeps_untouched_tags()
    {
        var current = TagTestHost.Tags(("env", "prod"), ("owner", "team-a"), ("cost", "1"), ("added-elsewhere", "x"));
        var merged = TagDiffBuilder.Apply(current, [new TagChange("owner", TagChangeKind.Update, "team-a", "team-b"), new TagChange("cost", TagChangeKind.Remove, "1", null)]);
        Assert.Equal(TagTestHost.Tags(("env", "prod"), ("owner", "team-b"), ("added-elsewhere", "x")), merged);
    }

    [Fact]
    public void Conflicts_only_count_tags_that_were_changed()
    {
        var current = TagTestHost.Tags(("env", "test"), ("owner", "team-a"), ("cost", "1"), ("other", "new"));
        var changes = new[] { new TagChange("env", TagChangeKind.Update, "prod", "staging"), new TagChange("owner", TagChangeKind.Update, "team-a", "team-b") };
        var conflict = Assert.Single(TagDiffBuilder.FindConflicts(Baseline, current, changes));
        Assert.Equal(new TagConflict("env", "prod", "test", "staging"), conflict);

        // The same change already made elsewhere is not a conflict.
        Assert.Empty(TagDiffBuilder.FindConflicts(Baseline, TagTestHost.Tags(("env", "staging")), [new TagChange("env", TagChangeKind.Update, "prod", "staging")]));
    }
}

public class ConnectionTagGeneratorTests
{
    [Fact]
    public void Chunks_values_without_splitting_names()
    {
        var names = Enumerable.Range(0, 30).Select(i => $"name-{i:00}").ToList(); // 7 characters each
        var chunks = ConnectionTagGenerator.Chunk(names, ";", 40);
        Assert.All(chunks, c => Assert.True(c.Length <= 40));
        Assert.Equal(names, chunks.SelectMany(c => c.Split(';')));
        Assert.Equal(["exactly-ten"], ConnectionTagGenerator.Chunk(["exactly-ten"], ";", 11));
        Assert.Equal("aaaaaaaaa…", Assert.Single(ConnectionTagGenerator.Chunk([new string('a', 20)], ";", 10)));
    }

    [Fact]
    public void Generates_out_and_in_tags_grouped_by_type()
    {
        var host = new TagTestHost();

        var shopWeb = Apply(host, Fixture.ShopWeb);
        Assert.Equal("contoso-sql/orders", shopWeb["conn-out-SqlDatabase"]);
        Assert.Equal("contoso-kv", shopWeb["conn-out-KeyVault"]);
        Assert.Contains("contosodata/uploads", shopWeb["conn-out-BlobContainer"]);
        Assert.Equal("2026-10-03T12:00:00Z", shopWeb["conn-updated"]);
        Assert.DoesNotContain(shopWeb.Keys, k => k.StartsWith("conn-in-", StringComparison.Ordinal));

        var orders = Apply(host, Fixture.OrdersDb);
        Assert.Equal("shop-web", orders["conn-in-WebApp"]);
        Assert.Equal("orders-func", orders["conn-in-FunctionApp"]);
        Assert.DoesNotContain(orders.Keys, k => k.StartsWith("conn-out-", StringComparison.Ordinal));

        var acr = Apply(host, Fixture.ContosoAcr);
        Assert.Equal("catalog-api", acr["conn-in-ContainerApp"]);
    }

    [Fact]
    public void Structural_edges_and_non_candidates_are_excluded()
    {
        var host = new TagTestHost();
        var server = Apply(host, Fixture.ContosoSql);
        Assert.DoesNotContain(server.Keys, k => k.Contains("SqlDatabase", StringComparison.Ordinal));

        Assert.False(host.Generator.Generate(host.Index, Fixture.Uploads, DateTimeOffset.UtcNow).IsValid);
        Assert.False(host.Generator.Generate(host.Index, "/subscriptions/x/nope", DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Regeneration_removes_stale_owned_tags_and_keeps_other_tags()
    {
        var snapshot = TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(
            ("env", "prod"), ("conn-out-RedisCache", "old-cache"), ("conn-out-SqlDatabase-2", "old"), ("conn-updated", "2020-01-01T00:00:00Z"))));
        var host = new TagTestHost(snapshot);

        var diff = host.Generator.Generate(host.Index, Fixture.ShopWeb, snapshot.ScannedUtc);

        Assert.Contains(diff.Changes, c => c is { Name: "conn-out-RedisCache", Kind: TagChangeKind.Remove });
        Assert.Contains(diff.Changes, c => c is { Name: "conn-out-SqlDatabase-2", Kind: TagChangeKind.Remove });
        Assert.Contains(diff.Changes, c => c is { Name: "conn-updated", Kind: TagChangeKind.Update });
        Assert.DoesNotContain(diff.Changes, c => c.Name == "env");

        // Running again on the result produces no changes (stable output).
        var applied = TagDiffBuilder.Apply(diff.BaselineTags, diff.Changes);
        TagSyncCoordinator.Apply(host.Session, Fixture.ShopWeb, applied);
        Assert.True(host.Generator.Generate(host.Index, Fixture.ShopWeb, snapshot.ScannedUtc).IsEmpty);
    }

    [Fact]
    public void Respects_the_50_tag_budget()
    {
        var existing = Enumerable.Range(0, 48).Select(i => ($"t{i}", "v")).ToArray();
        var snapshot = TagTestHost.SnapshotWithTags((Fixture.ShopWeb, TagTestHost.Tags(existing)));
        var host = new TagTestHost(snapshot);

        var diff = host.Generator.Generate(host.Index, Fixture.ShopWeb, snapshot.ScannedUtc);

        Assert.Equal(50, TagDiffBuilder.Apply(diff.BaselineTags, diff.Changes).Count);
        Assert.Contains(diff.Changes, c => c.Name == "conn-updated");
        Assert.Single(diff.Warnings);
    }

    [Fact]
    public void Candidates_filter_by_scope_and_type()
    {
        var host = new TagTestHost();
        var all = ConnectionTagGenerator.Candidates(host.Index, null, null, null);
        Assert.DoesNotContain(all, n => n.Type is "BlobContainer" or "External" or "ResourceGroup" or "Subscription" or "Tenant");
        Assert.All(ConnectionTagGenerator.Candidates(host.Index, null, "rg-data", null), n => Assert.Equal("rg-data", n.Data["resourceGroup"]));
        Assert.All(ConnectionTagGenerator.Candidates(host.Index, null, null, ["WebApp"]), n => Assert.Equal("WebApp", n.Type));
        Assert.Empty(ConnectionTagGenerator.Candidates(host.Index, "00000000-0000-0000-0000-000000000000", null, null));
    }

    private static Dictionary<string, string> Apply(TagTestHost host, string id)
    {
        var diff = host.Generator.Generate(host.Index, id, host.Session.Snapshot!.ScannedUtc);
        Assert.True(diff.IsValid, string.Join(" ", diff.Errors));
        return TagDiffBuilder.Apply(diff.BaselineTags, diff.Changes);
    }
}

public class TagMappingTests
{
    [Fact]
    public void Reads_tags_from_resource_graph_rows()
    {
        using var doc = JsonDocument.Parse("""{ "tags": { "Env": "prod", "env": "dup", "count": 3, "empty": null } }""");
        var tags = ResourceGraphInventoryService.ReadTags(doc.RootElement);
        Assert.Equal("prod", tags["ENV"]);
        Assert.Equal("3", tags["count"]);
        Assert.Equal("", tags["empty"]);
        Assert.Equal(3, tags.Count);

        using var none = JsonDocument.Parse("""{ "tags": null }""");
        Assert.Empty(ResourceGraphInventoryService.ReadTags(none.RootElement));
    }

    [Fact]
    public void Maps_arm_errors_to_outcomes()
    {
        var policy = new RequestFailedException(403,
            "Resource was disallowed by policy. Policy identifiers: '[{\"policyAssignment\":{\"name\":\"Require cost tag\",\"id\":\"/x\"}}]'.",
            "RequestDisallowedByPolicy", null);
        Assert.Equal((TagWriteOutcome.PolicyDenied, "Blocked by policy 'Require cost tag'."), TagErrorMapper.Map(policy));
        Assert.Equal(TagWriteOutcome.Forbidden, TagErrorMapper.Map(new RequestFailedException(403, "x", "AuthorizationFailed", null)).Outcome);
        Assert.Equal(TagWriteOutcome.NotFound, TagErrorMapper.Map(new RequestFailedException(404, "x")).Outcome);
        Assert.Equal(TagWriteOutcome.Throttled, TagErrorMapper.Map(new RequestFailedException(429, "x")).Outcome);
        Assert.Equal((TagWriteOutcome.Failed, "The resource is locked (ReadOnly lock)."), TagErrorMapper.Map(new RequestFailedException(409, "x", "ScopeLocked", null)));
        Assert.Equal(TagWriteOutcome.ValidationFailed, TagErrorMapper.Map(new RequestFailedException(400, "x", "InvalidTagNameCharacters", null)).Outcome);
    }

    [Theory]
    [InlineData("""{"value":[{"actions":["*/read"],"notActions":[]}]}""", false)]
    [InlineData("""{"value":[{"actions":["*/read"]},{"actions":["Microsoft.Resources/tags/*"]}]}""", true)]
    [InlineData("""{"value":[{"actions":["*"],"notActions":["Microsoft.Resources/tags/write"]}]}""", false)]
    [InlineData("""{"value":[{"actions":["*"],"notActions":["Microsoft.Authorization/*/Write"]}]}""", true)]
    public void Evaluates_tag_write_permission(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, TagPermissionChecker.Evaluate(doc.RootElement, TagPermissionChecker.TagWriteAction));
    }

    [Fact]
    public void Permission_scope_is_the_resource_group_or_subscription()
    {
        Assert.Equal($"/subscriptions/{Fixture.SubscriptionId}/resourceGroups/rg-shop", TagPermissionChecker.ScopeFor(Fixture.ShopWeb));
        Assert.Equal("/subscriptions/abc", TagPermissionChecker.ScopeFor("/subscriptions/abc"));
        Assert.Null(TagPermissionChecker.ScopeFor("tenant:abc"));
    }
}
