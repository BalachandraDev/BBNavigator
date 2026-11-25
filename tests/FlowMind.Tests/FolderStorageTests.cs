using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Security;
using FlowMind.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

/// <summary>"Folder on a server" storage: which paths a company admin may type, how the folder is used, and the Settings flow end to end.</summary>
public class FolderGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-folder-" + Guid.NewGuid().ToString("N")[..8]);
    public FolderGuardTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void Folder_storage_is_off_until_the_operator_lists_allowed_roots()
    {
        Assert.Null(FolderGuard.Check(_root, Array.Empty<string>(), out var error));
        Assert.Contains("switched off", error);
        Assert.Null(FolderGuard.Check(_root, new[] { "", "  " }, out error));
        Assert.Contains("switched off", error);
    }

    [Fact]
    public void A_folder_inside_an_allowed_root_is_accepted_and_normalised()
    {
        Assert.Equal(_root, FolderGuard.Check(_root, new[] { _root }, out var e1)); Assert.Null(e1);                                  // the root itself
        Assert.Equal(Path.Combine(_root, "hospital-a"), FolderGuard.Check(Path.Combine(_root, "hospital-a") + Path.DirectorySeparatorChar, new[] { _root }, out _));   // a sub-folder; trailing slash removed
        Assert.NotNull(FolderGuard.Check("  " + Path.Combine(_root, "a", "b") + "  ", new[] { _root }, out _));                       // surrounding spaces ignored
    }

    [Fact]
    public void Anything_outside_the_allowed_roots_is_refused()
    {
        var roots = new[] { _root };
        Assert.Contains("not in a location", Assert.IsType<string>(Check(Path.GetTempPath(), roots)));                                // the parent folder
        Assert.Contains("not in a location", Assert.IsType<string>(Check(_root + "-other", roots)));                                  // a sibling that merely starts with the same letters
        Assert.Contains("not in a location", Assert.IsType<string>(Check(Path.GetFullPath(Path.Combine(Environment.SystemDirectory)), roots)));   // a system folder
    }

    [Theory]
    [InlineData("relative/folder", "full path")]
    [InlineData("docs", "full path")]
    [InlineData("", "Enter the folder path")]
    [InlineData("   ", "Enter the folder path")]
    public void Relative_or_empty_paths_are_refused(string input, string message)
    {
        Assert.Contains(message, Check(input, new[] { Path.GetTempPath() })!);
    }

    [Fact]
    public void Dot_dot_wildcards_and_control_characters_are_refused_before_anything_else()
    {
        var roots = new[] { _root };
        Assert.Contains("\"..\"", Check(Path.Combine(_root, "a", "..", "..", "x"), roots)!);
        Assert.Contains("\"..\"", Check(_root + "/../escape", roots)!);
        Assert.Contains("not allowed", Check(Path.Combine(_root, "a*b"), roots)!);
        Assert.Contains("not allowed", Check(Path.Combine(_root, "a?b"), roots)!);
        Assert.Contains("not allowed", Check(Path.Combine(_root, "a|b"), roots)!);
        Assert.Contains("not allowed", Check(Path.Combine(_root, "a\u0000b"), roots)!);
        Assert.Contains("too long", Check(Path.Combine(_root, new string('x', 400)), roots)!);
    }

    private static string? Check(string path, string[] roots) { FolderGuard.Check(path, roots, out var error); return error; }
}

public class FolderObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-store-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "fm-src-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
    public FolderObjectStoreTests() { Directory.CreateDirectory(_root); File.WriteAllText(_tmp, "hello folder"); }
    public void Dispose() { try { Directory.Delete(_root, true); File.Delete(_tmp); } catch { /* best effort */ } }

    [Fact]
    public async Task Files_are_written_read_back_and_removed_inside_the_folder()
    {
        var s = new FolderObjectStore(_root);
        await s.PutFileAsync("tenant/doc/v1/original.txt", _tmp, default);
        Assert.True(File.Exists(Path.Combine(_root, "tenant", "doc", "v1", "original.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tenant", "doc", "v1"), "*.part"));                  // no half-written leftovers
        using (var r = new StreamReader(await s.GetAsync("tenant/doc/v1/original.txt", default))) Assert.Equal("hello folder", await r.ReadToEndAsync());

        await s.PutFileAsync("tenant/doc/v1/original.txt", _tmp, default);                                       // overwriting is fine
        await s.DeleteAsync("tenant/doc/v1/original.txt", default);
        Assert.False(File.Exists(Path.Combine(_root, "tenant", "doc", "v1", "original.txt")));

        await s.PutFileAsync("tenant/doc2/v1/original.txt", _tmp, default);
        await s.DeletePrefixAsync("tenant/doc2/", default);
        Assert.False(Directory.Exists(Path.Combine(_root, "tenant", "doc2")));
        Assert.True(Directory.Exists(_root));                                                                    // the chosen folder itself is never removed
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("tenant/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task A_key_can_never_leave_the_folder(string key)
    {
        var s = new FolderObjectStore(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PutFileAsync(key, _tmp, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetAsync(key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteAsync(key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeletePrefixAsync(key, default));
    }

    [Fact]
    public async Task The_whole_folder_can_not_be_removed_through_a_prefix()
    {
        var s = new FolderObjectStore(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeletePrefixAsync("", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeletePrefixAsync("/", default));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task The_test_writes_reads_and_removes_a_file_and_explains_a_missing_folder()
    {
        var ok = await new FolderObjectStore(_root).ProbeAsync("", default);
        Assert.True(ok.Ok); Assert.Contains("works", ok.Message);
        Assert.Empty(Directory.GetFiles(_root));                                                                  // the test file is gone

        var missing = await new FolderObjectStore(Path.Combine(_root, "does-not-exist")).ProbeAsync("", default);
        Assert.False(missing.Ok); Assert.Contains("not found", missing.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "does-not-exist")));                                    // a typo never creates a stray folder
    }
}

public class FolderStorageApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _f;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-api-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderStorageApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_root);
        _f = factory.Configure().WithWebHostBuilder(b => b.UseSetting("Storage:AllowedFolderRoots:0", _root));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private Guid SeedTenant()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var t = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = t, Name = "Acme", Slug = "fo-" + t.ToString("N")[..8], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        db.SaveChanges();
        return t;
    }
    private HttpClient Client(Guid tenant, string role = Roles.TenantAdmin)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", role, tenant).Token);
        return c;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<Guid> NewApp(HttpClient admin) => (await Json(await admin.PostAsJsonAsync("/api/v1/applications", new { name = "App" }))).GetProperty("id").GetGuid();
    private static async Task<HttpResponseMessage> Upload(HttpClient admin, Guid app, string name, string text)
    {
        var form = new MultipartFormDataContent { { new StringContent(app.ToString()), "ApplicationId" }, { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "File", name } };
        return await admin.PostAsync("/api/v1/documents", form);
    }
    private static object Folder(string path) => new { provider = "folder", folderPath = path };

    [Fact]
    public async Task The_settings_page_offers_the_folder_option_and_lists_where_it_may_point()
    {
        var admin = Client(SeedTenant());
        var v = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.True(v.GetProperty("folderEnabled").GetBoolean());
        Assert.Equal(_root, v.GetProperty("folderRoots")[0].GetString());
        Assert.Equal("local", v.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task A_folder_is_only_saved_when_allowed_existing_and_writable_and_the_test_leaves_nothing_behind()
    {
        var admin = Client(SeedTenant());

        var outside = await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(Path.GetTempPath()));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode); Assert.Contains("not in a location", await outside.Content.ReadAsStringAsync());
        var dots = await admin.PostAsJsonAsync("/api/v1/settings/storage/test", Folder(_root + "/../escape"));
        Assert.Equal(HttpStatusCode.BadRequest, dots.StatusCode);
        var relative = await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder("docs"));
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);

        var missing = Path.Combine(_root, "not-created-yet");
        var notFound = await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(missing));
        Assert.Equal(HttpStatusCode.BadRequest, notFound.StatusCode); Assert.Contains("not found", await notFound.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(missing));
        Assert.Equal("local", (await Json(await admin.GetAsync("/api/v1/settings/storage"))).GetProperty("provider").GetString());   // nothing was saved

        var good = Path.Combine(_root, "hospital-a"); Directory.CreateDirectory(good);
        var test = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/test", Folder(good)));
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Empty(Directory.GetFileSystemEntries(good));                                                                           // the test file was removed
        var saved = await Json(await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(good)));
        Assert.Equal("folder", saved.GetProperty("provider").GetString()); Assert.Equal(good, saved.GetProperty("folderPath").GetString());
    }

    [Fact]
    public async Task New_uploads_go_to_the_folder_under_the_company_and_files_can_be_copied_each_way()
    {
        var t = SeedTenant(); var admin = Client(t); var app = await NewApp(admin);
        var folder = Path.Combine(_root, "docs-" + t.ToString("N")[..6]); Directory.CreateDirectory(folder);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(admin, app, "first.txt", "Registration page: the nurse enters the name.")).StatusCode);   // default: this server

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(folder))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(admin, app, "second.txt", "Billing page: the cashier prints the invoice.")).StatusCode);
        var stored = Directory.GetFiles(Path.Combine(folder, t.ToString("N")), "original.txt", SearchOption.AllDirectories);
        Assert.Single(stored);                                                                                                         // only the new file, inside the company's own sub-folder
        Assert.Equal("Billing page: the cashier prints the invoice.", File.ReadAllText(stored[0]));

        var view = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.Equal(1, view.GetProperty("local").GetProperty("files").GetInt32()); Assert.Equal(1, view.GetProperty("folder").GetProperty("files").GetInt32());

        var up = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "folder", removeSource = false, take = 10 }));
        Assert.Equal(1, up.GetProperty("moved").GetInt32()); Assert.Equal(0, up.GetProperty("remaining").GetInt32()); Assert.Equal(0, up.GetProperty("failed").GetArrayLength());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, t.ToString("N")), "original.txt", SearchOption.AllDirectories).Length);

        var down = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "local", removeSource = true, take = 10 }));
        Assert.Equal(2, down.GetProperty("moved").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, t.ToString("N")), "original.txt", SearchOption.AllDirectories));          // removed from the folder after the verified copy
        var end = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.Equal(2, end.GetProperty("local").GetProperty("files").GetInt32()); Assert.Equal(0, end.GetProperty("folder").GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task Copying_to_the_folder_needs_a_saved_folder_and_a_folder_that_stops_being_allowed_is_refused_clearly()
    {
        var t = SeedTenant(); var admin = Client(t); var app = await NewApp(admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "folder", take = 10 })).StatusCode);

        var folder = Path.Combine(_root, "later-" + t.ToString("N")[..6]); Directory.CreateDirectory(folder);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(folder))).StatusCode);

        // the operator removes the root later: the saved path must stop working (the stored value is re-checked on every use)
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var s = db.StorageSettings.IgnoreQueryFilters().Single(x => x.TenantId == t);
        s.FolderPath = Path.Combine(Path.GetTempPath(), "elsewhere"); db.SaveChanges();
        var r = await Upload(admin, app, "blocked.txt", "Some text for the document.");
        Assert.NotEqual(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("no longer allowed", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_a_company_removes_its_sub_folder_but_never_the_chosen_folder()
    {
        var t = SeedTenant(); var admin = Client(t); var app = await NewApp(admin);
        var folder = Path.Combine(_root, "wipe-" + t.ToString("N")[..6]); Directory.CreateDirectory(folder);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Folder(folder))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(admin, app, "a.txt", "Some text for the document.")).StatusCode);
        Assert.True(Directory.Exists(Path.Combine(folder, t.ToString("N"))));

        using var scope = _f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().DeleteTenantAsync(t, default);
        Assert.False(Directory.Exists(Path.Combine(folder, t.ToString("N"))));
        Assert.True(Directory.Exists(folder));
    }
}

/// <summary>Without operator roots the option exists but is switched off, and no typed path can change that.</summary>
public class FolderStorageOffApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _f;
    public FolderStorageOffApiTests(WebApplicationFactory<Program> factory) => _f = factory.Configure();

    [Fact]
    public async Task Folder_storage_cannot_be_used_until_the_server_operator_allows_a_location()
    {
        Guid t;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            t = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = t, Name = "Acme", Slug = "fx-" + t.ToString("N")[..8], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
            db.SaveChanges();
        }
        var admin = _f.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", Roles.TenantAdmin, t).Token);

        var v = await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings/storage");
        Assert.False(v.GetProperty("folderEnabled").GetBoolean()); Assert.Equal(0, v.GetProperty("folderRoots").GetArrayLength());
        var r = await admin.PutAsJsonAsync("/api/v1/settings/storage", new { provider = "folder", folderPath = Path.GetTempPath() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Contains("switched off", await r.Content.ReadAsStringAsync());
        Assert.Equal("local", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings/storage")).GetProperty("provider").GetString());
    }
}

/// <summary>The platform administrator approves locations in the app (no server configuration needed); companies then choose a folder inside one.</summary>
public class FolderApprovalApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _f;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-appr-" + Guid.NewGuid().ToString("N")[..8]);
    public FolderApprovalApiTests(WebApplicationFactory<Program> factory) { Directory.CreateDirectory(_root); _f = factory.Configure(); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private HttpClient Platform()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("platform-admin", Roles.PlatformAdmin, null).Token);
        return c;
    }
    private HttpClient Company(out Guid tenant)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Acme", Slug = "fa-" + tenant.ToString("N")[..8], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        db.SaveChanges();
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", Roles.TenantAdmin, tenant).Token);
        return c;
    }
    private static object Folder(string path) => new { provider = "folder", folderPath = path };

    [Fact]
    public async Task A_company_cannot_approve_locations_only_the_platform_admin_can()
    {
        var company = Company(out _);
        Assert.Equal(HttpStatusCode.Forbidden, (await company.PostAsJsonAsync("/api/v1/server-folders", new { path = _root })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await company.GetAsync("/api/v1/server-folders")).StatusCode);
    }

    [Fact]
    public async Task Approving_a_location_switches_folder_storage_on_for_companies_and_removing_it_is_blocked_while_in_use()
    {
        var company = Company(out var t); var admin = Platform();
        var mine = Path.Combine(_root, "company-files"); Directory.CreateDirectory(mine);

        Assert.False((await company.GetFromJsonAsync<JsonElement>("/api/v1/settings/storage")).GetProperty("folderEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await company.PutAsJsonAsync("/api/v1/settings/storage", Folder(mine))).StatusCode);        // off until approved

        var add = await admin.PostAsJsonAsync("/api/v1/server-folders", new { path = _root });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(_root).Where(x => Path.GetFileName(x).StartsWith(".flowmind-test")));                       // the proving file is gone
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/server-folders", new { path = _root })).StatusCode);        // no duplicates

        var view = await company.GetFromJsonAsync<JsonElement>("/api/v1/settings/storage");
        Assert.True(view.GetProperty("folderEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await company.PutAsJsonAsync("/api/v1/settings/storage", Folder(mine))).StatusCode);                  // typed in Settings, inside the approved location
        Assert.Equal(HttpStatusCode.BadRequest, (await company.PutAsJsonAsync("/api/v1/settings/storage", Folder(Path.GetTempPath()))).StatusCode);   // outside it

        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/v1/server-folders");
        var row = rows.EnumerateArray().Single(r => r.GetProperty("path").GetString() == _root);
        Assert.Equal(1, row.GetProperty("companies").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/v1/server-folders/" + row.GetProperty("id").GetGuid())).StatusCode);

        using (var scope = _f.Services.CreateScope())                                                                                          // the company moves away: removal now works
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            db.StorageSettings.IgnoreQueryFilters().Single(s => s.TenantId == t).FolderPath = ""; db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/server-folders/" + row.GetProperty("id").GetGuid())).StatusCode);
        Assert.False((await company.GetFromJsonAsync<JsonElement>("/api/v1/settings/storage")).GetProperty("folderEnabled").GetBoolean());
    }

    [Theory]
    [InlineData("docs")]                       // relative
    [InlineData("C:\\")]                       // a whole drive
    [InlineData("/")]
    public async Task Unsafe_or_missing_locations_are_refused(string path)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Platform().PostAsJsonAsync("/api/v1/server-folders", new { path })).StatusCode);
    }

    [Fact]
    public async Task A_location_that_does_not_exist_is_refused_and_never_created()
    {
        var missing = Path.Combine(_root, "not-there");
        var r = await Platform().PostAsJsonAsync("/api/v1/server-folders", new { path = missing });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Contains("not found", await r.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void System_folders_are_not_offered_as_locations()
    {
        var sys = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "/etc";
        Assert.Null(FolderRoots.CheckLocation(sys, out var why)); Assert.Contains("system", why);
    }
}