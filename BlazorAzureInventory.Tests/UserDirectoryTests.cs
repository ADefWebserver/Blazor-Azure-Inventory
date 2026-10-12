using BlazorAzureInventory.Models.Options;
using BlazorAzureInventory.Models.Users;
using BlazorAzureInventory.Services.Persistence;
using BlazorAzureInventory.Services.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorAzureInventory.Tests;

public class UserDirectoryTests
{
    private const string Tenant = Fixture.TenantId;
    private static readonly AuditActor System = AuditActor.System;

    private sealed class Host
    {
        public InMemoryUserDirectoryStore Store { get; }
        public UserDirectory Directory { get; }

        public Host(string bootstrap = "admin@contoso.com, second@contoso.com", SecurityOptions? security = null, bool canWrite = true)
        {
            Store = new InMemoryUserDirectoryStore(canWrite);
            var options = new UserManagementOptions { BootstrapAdmins = bootstrap, DirectoryRefreshSeconds = 5 };
            var audit = new AuditLog(new PersistencePaths(Path.GetTempPath()), new PersistenceStatus(false, "test"), TimeProvider.System, NullLogger<AuditLog>.Instance);
            Directory = new UserDirectory(Store, audit, new StaticMonitor<UserManagementOptions>(options),
                Options.Create(security ?? new SecurityOptions()), TimeProvider.System, NullLogger<UserDirectory>.Instance);
        }

        public async Task<AuditActor> SignInAdminAsync(string username = "admin@contoso.com")
        {
            var oid = Guid.NewGuid().ToString();
            var decision = await Directory.SignInAsync(Tenant, oid, username, "Admin", CancellationToken.None);
            Assert.True(decision.Allowed);
            return new AuditActor(Tenant, oid, username);
        }

        public UserRecord User(string username) => Directory.Users.Single(u => u.Username == username);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task Seeds_bootstrap_admins_and_migrates_legacy_allow_list()
    {
        var host = new Host(security: new SecurityOptions
        {
            UsernameAllowListEnabled = true,
            AllowedUsernames = "Admin@contoso.com, bob@contoso.com, not-an-email, carol@contoso.com"
        });

        await host.Directory.EnsureSeededAsync(CancellationToken.None);

        var users = host.Directory.Users;
        Assert.Equal(4, users.Count);
        Assert.All(users.Where(u => u.IsBootstrap), u => Assert.Equal(AppRoles.Admin, u.Role));
        Assert.Equal(AppRoles.User, host.User("bob@contoso.com").Role);
        Assert.All(users, u => Assert.Equal(UserStatus.Pending, u.Status));
    }

    [Fact]
    public async Task Pending_user_binds_on_first_sign_in_and_is_then_matched_by_object_id()
    {
        var host = new Host();
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["Bob@Contoso.com"], AppRoles.User, CancellationToken.None);
        var oid = Guid.NewGuid().ToString();

        var first = await host.Directory.SignInAsync(Tenant, oid, "bob@contoso.com", "Bob", CancellationToken.None);
        var renamed = await host.Directory.SignInAsync(Tenant, oid, "robert@contoso.com", "Bob", CancellationToken.None);

        Assert.Equal(SignInDecision.Allow(AppRoles.User), first);
        Assert.Equal(SignInDecision.Allow(AppRoles.User), renamed);
        Assert.Equal(UserStatus.Active, host.User("bob@contoso.com").Status);
        Assert.Equal(AppRoles.User, host.Directory.GetActiveRole(Tenant, oid, "anything"));
    }

    [Fact]
    public async Task Same_username_with_different_object_id_is_denied()
    {
        var host = new Host();
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["bob@contoso.com"], AppRoles.User, CancellationToken.None);
        await host.Directory.SignInAsync(Tenant, Guid.NewGuid().ToString(), "bob@contoso.com", null, CancellationToken.None);

        var second = await host.Directory.SignInAsync(Tenant, Guid.NewGuid().ToString(), "bob@contoso.com", null, CancellationToken.None);

        Assert.Equal(SignInDecision.Deny(DenyReasons.Mismatch), second);
    }

    [Fact]
    public async Task Unknown_and_disabled_users_are_denied()
    {
        var host = new Host();
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["bob@contoso.com"], AppRoles.User, CancellationToken.None);
        var bob = Guid.NewGuid().ToString();
        await host.Directory.SignInAsync(Tenant, bob, "bob@contoso.com", null, CancellationToken.None);
        await host.Directory.SetEnabledAsync(admin, host.User("bob@contoso.com").Id, false, CancellationToken.None);

        Assert.Equal(SignInDecision.Deny(DenyReasons.NotInvited),
            await host.Directory.SignInAsync(Tenant, Guid.NewGuid().ToString(), "eve@contoso.com", null, CancellationToken.None));
        Assert.Equal(SignInDecision.Deny(DenyReasons.Disabled),
            await host.Directory.SignInAsync(Tenant, bob, "bob@contoso.com", null, CancellationToken.None));
        Assert.Null(host.Directory.GetActiveRole(Tenant, bob, "bob@contoso.com"));
    }

    [Fact]
    public async Task Guard_rails_protect_self_bootstrap_and_last_admin()
    {
        var host = new Host(bootstrap: "admin@contoso.com");
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["ops@contoso.com"], AppRoles.Admin, CancellationToken.None);
        var ops = await host.SignInAdminAsync("ops@contoso.com");
        var adminId = host.User("admin@contoso.com").Id;
        var opsId = host.User("ops@contoso.com").Id;

        var self = await Assert.ThrowsAsync<UserDirectoryException>(() => host.Directory.SetRoleAsync(ops, opsId, AppRoles.User, CancellationToken.None));
        var bootstrap = await Assert.ThrowsAsync<UserDirectoryException>(() => host.Directory.RemoveAsync(ops, adminId, CancellationToken.None));

        Assert.Contains("own access", self.Message);
        Assert.Contains("deployment settings", bootstrap.Message);
        await host.Directory.SetRoleAsync(admin, opsId, AppRoles.User, CancellationToken.None);
        Assert.Equal(AppRoles.User, host.User("ops@contoso.com").Role);
    }

    [Fact]
    public async Task Disabled_admin_loses_admin_rights()
    {
        var host = new Host(bootstrap: "admin@contoso.com");
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["ops@contoso.com"], AppRoles.Admin, CancellationToken.None);
        var ops = await host.SignInAdminAsync("ops@contoso.com");

        await host.Directory.SetEnabledAsync(admin, host.User("ops@contoso.com").Id, false, CancellationToken.None);

        Assert.Null(host.Directory.GetActiveRole(ops.TenantId, ops.ObjectId, ops.Username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Directory.AddUsersAsync(ops, ["eve@contoso.com"], AppRoles.User, CancellationToken.None));
    }

    [Fact]
    public async Task Non_admins_cannot_manage_users()
    {
        var host = new Host();
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();
        await host.Directory.AddUsersAsync(admin, ["bob@contoso.com"], AppRoles.User, CancellationToken.None);
        var bobOid = Guid.NewGuid().ToString();
        await host.Directory.SignInAsync(Tenant, bobOid, "bob@contoso.com", null, CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Directory.AddUsersAsync(new AuditActor(Tenant, bobOid, "bob@contoso.com"), ["eve@contoso.com"], AppRoles.Admin, CancellationToken.None));
    }

    [Fact]
    public async Task Add_reports_existing_and_invalid_usernames()
    {
        var host = new Host();
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        var admin = await host.SignInAdminAsync();

        var result = await host.Directory.AddUsersAsync(admin, ["bob@contoso.com", "BOB@contoso.com", "admin@contoso.com", "nope", "a b@c.com"], AppRoles.User, CancellationToken.None);

        Assert.Equal(["bob@contoso.com"], result.Added);
        Assert.Equal(["admin@contoso.com"], result.Existing);
        Assert.Equal(2, result.Invalid.Count);
    }

    [Fact]
    public async Task Without_writable_storage_only_bootstrap_admins_sign_in()
    {
        var host = new Host(canWrite: false);
        await host.Directory.EnsureSeededAsync(CancellationToken.None);

        Assert.Equal(SignInDecision.Allow(AppRoles.Admin),
            await host.Directory.SignInAsync(Tenant, Guid.NewGuid().ToString(), "admin@contoso.com", null, CancellationToken.None));
        Assert.Equal(SignInDecision.Deny(DenyReasons.Unavailable),
            await host.Directory.SignInAsync(Tenant, Guid.NewGuid().ToString(), "bob@contoso.com", null, CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_admin_is_restored_after_being_disabled_in_the_file()
    {
        var host = new Host(bootstrap: "admin@contoso.com");
        await host.Directory.EnsureSeededAsync(CancellationToken.None);
        await host.SignInAdminAsync();
        await host.Store.UpdateAsync(d => { d!.Users.ForEach(u => { u.Status = UserStatus.Disabled; u.Role = AppRoles.User; }); return d; }, CancellationToken.None);

        await host.Directory.EnsureSeededAsync(CancellationToken.None);

        var admin = host.User("admin@contoso.com");
        Assert.Equal(AppRoles.Admin, admin.Role);
        Assert.Equal(UserStatus.Active, admin.Status);
    }

    [Fact]
    public async Task File_store_serializes_concurrent_updates()
    {
        using var root = new TempRoot();
        var store = new FileUserDirectoryStore(root.Paths);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.UpdateAsync(d =>
        {
            var doc = d ?? new UserDirectoryDocument();
            doc.Users.Add(new UserRecord { Username = $"u{i}@contoso.com" });
            return doc;
        }, CancellationToken.None)));

        var final = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(10, final!.Users.Count);
        Assert.Equal(10, final.Version);
    }
}
