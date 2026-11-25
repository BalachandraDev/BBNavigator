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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

/// <summary>A bucket that lives in memory, so storage tests never need a cloud account.</summary>
public sealed class MemoryStore : IObjectStore
{
    public readonly Dictionary<string, byte[]> Objects = new();
    public bool FailPut;
    public string? ProbeFailure;
    public Task PutFileAsync(string key, string filePath, CancellationToken ct)
    {
        if (FailPut) throw new IOException("bucket unreachable");
        Objects[key] = File.ReadAllBytes(filePath); return Task.CompletedTask;
    }
    public Task<Stream> GetAsync(string key, CancellationToken ct) =>
        Objects.TryGetValue(key, out var b) ? Task.FromResult<Stream>(new MemoryStream(b)) : throw new FileNotFoundException(key);
    public Task DeleteAsync(string key, CancellationToken ct) { Objects.Remove(key); return Task.CompletedTask; }
    public Task DeletePrefixAsync(string prefix, CancellationToken ct) { foreach (var k in Objects.Keys.Where(k => k.StartsWith(prefix)).ToList()) Objects.Remove(k); return Task.CompletedTask; }
    public Task<StorageProbeResult> ProbeAsync(string keyPrefix, CancellationToken ct) =>
        Task.FromResult(ProbeFailure == null ? new StorageProbeResult(true, "Connected. A test file was written, read back and removed.", 12) : new StorageProbeResult(false, ProbeFailure, 3));
}
public sealed class MemoryStoreFactory : IObjectStoreFactory
{
    public readonly MemoryStore Store = new();
    public readonly List<StorageConnection> Seen = new();
    /// <summary>Keys starting with BAD are refused, like a bucket that does not know that key.</summary>
    public IObjectStore Create(StorageConnection c)
    {
        Seen.Add(c);
        if (c.AccessKey.StartsWith("BAD")) return new MemoryStore { ProbeFailure = "The access key id was not recognised. Check it for typing mistakes." };
        return Store;
    }
}

public class EndpointGuardTests
{
    private static System.Net.IPAddress[] Public(string _) => new[] { System.Net.IPAddress.Parse("104.18.2.2") };
    private static System.Net.IPAddress[] Private(string _) => new[] { System.Net.IPAddress.Parse("10.1.2.3") };

    [Theory]
    [InlineData("https://abc.r2.cloudflarestorage.com")]
    [InlineData("https://s3.eu-west-1.amazonaws.com")]
    [InlineData("https://203.0.113.9")]
    public void Public_https_addresses_are_accepted(string url) => Assert.Null(EndpointGuard.Check(url, false, Public));

    [Theory]
    [InlineData("http://storage.example.com")]                 // not https
    [InlineData("https://localhost")]
    [InlineData("https://minio")]                              // no dot: a LAN host name
    [InlineData("https://files.local")]
    [InlineData("https://127.0.0.1:9000")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://192.168.1.20")]
    [InlineData("https://172.20.0.4")]
    [InlineData("https://169.254.169.254")]                    // cloud metadata address
    [InlineData("https://[::1]")]
    [InlineData("https://user:pass@storage.example.com")]
    [InlineData("ftp://storage.example.com")]
    [InlineData("not a url")]
    public void Private_plain_or_malformed_addresses_are_refused(string url) => Assert.NotNull(EndpointGuard.Check(url, false, Public));

    [Fact]
    public void A_public_looking_name_that_resolves_to_a_private_address_is_refused() =>
        Assert.NotNull(EndpointGuard.Check("https://storage.example.com", false, Private));

    [Fact]
    public void The_operator_can_allow_a_private_network_but_never_a_broken_address()
    {
        Assert.Null(EndpointGuard.Check("http://192.168.1.20:9000", true, Private));
        Assert.Null(EndpointGuard.Check("https://minio:9000", true, Private));
        Assert.NotNull(EndpointGuard.Check("ftp://192.168.1.20", true, Private));
        Assert.NotNull(EndpointGuard.Check("http://user:pw@192.168.1.20", true, Private));
    }
}

/// <summary>The router: each company's files go where Settings says, and every file remembers where it is.</summary>
public class RoutedStorageTests : IDisposable
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-store-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _tenant = Guid.NewGuid(), _doc = Guid.NewGuid();
    private readonly FlowMindDbContext _db = new(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
    private readonly SecretBox _box = new("a-server-secret-that-is-long-enough-123");
    private readonly MemoryStoreFactory _factory = new();

    private RoutedFileStorage Router() => new(new LocalFileStorage(Options.Create(new StorageOptions { RootPath = _root })), _db, _box, _factory, NullLogger<RoutedFileStorage>.Instance);
    private void UseCloud(string prefix = "hms", string provider = "r2", string accessKey = "AKIA1234")
    {
        _db.StorageSettings.Add(new StorageSetting
        {
            TenantId = _tenant, Provider = provider, RemoteType = "r2", Endpoint = "https://" + new string('a', 32) + ".r2.cloudflarestorage.com", Bucket = "b", Region = "auto", Prefix = prefix,
            AccessKeyCipher = _box.Encrypt(accessKey), SecretKeyCipher = _box.Encrypt("secret"),
        });
        _db.SaveChanges();
    }
    private async Task<StagedFile> Stage(string text) => await Router().StageAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), 1_000_000, default);
    private static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task<string> Read(Stream s) { await using (s) { using var r = new StreamReader(s); return await r.ReadToEndAsync(); } }
    private string RelPath(int v = 1) => $"{_tenant:N}/{_doc:N}/v{v}/original.txt";

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp folder */ } }

    [Fact]
    public async Task Without_any_setting_files_stay_on_this_server()
    {
        var r = Router();
        var stored = await r.CommitAsync(await Stage("hello"), _tenant, _doc, 1, "txt", default);
        Assert.Equal(new StoredFile(RelPath(), "local"), stored);
        Assert.Equal("hello", await Read(await r.OpenReadAsync(stored.Path, stored.Kind, _tenant, default)));
        Assert.Empty(_factory.Store.Objects);
    }

    [Fact]
    public async Task With_cloud_storage_chosen_new_files_go_to_the_bucket_under_the_prefix_and_not_to_this_server()
    {
        UseCloud("hms");
        var r = Router();
        var staged = await Stage("cloud text");
        var stored = await r.CommitAsync(staged, _tenant, _doc, 1, "txt", default);

        Assert.Equal(new StoredFile(RelPath(), "remote"), stored);
        Assert.Equal("cloud text", Encoding.UTF8.GetString(_factory.Store.Objects["hms/" + RelPath()]));
        Assert.False(File.Exists(Path.Combine(_root, _tenant.ToString("N"), _doc.ToString("N"), "v1", "original.txt")));   // nothing left on this server
        Assert.False(File.Exists(staged.TempPath));                                                                      // and the temp copy is gone
        Assert.Equal("cloud text", await Read(await r.OpenReadAsync(stored.Path, stored.Kind, _tenant, default)));
        Assert.Equal("AKIA1234", _factory.Seen.Last().AccessKey);                                                         // the saved key was decrypted for the call
    }

    [Fact]
    public async Task An_unusable_cloud_is_reported_and_never_replaced_by_quietly_saving_locally()
    {
        _db.StorageSettings.Add(new StorageSetting { TenantId = _tenant, Provider = "r2", RemoteType = "r2", Endpoint = "https://x.example.com", Bucket = "b" });   // no keys
        _db.SaveChanges();
        var r = Router();
        await Assert.ThrowsAsync<StorageUnavailableException>(async () => await r.CommitAsync(await Stage("x"), _tenant, _doc, 1, "txt", default));
        Assert.False(Directory.Exists(Path.Combine(_root, _tenant.ToString("N"))));

        _db.StorageSettings.RemoveRange(_db.StorageSettings); _db.SaveChanges();
        UseCloud(); _factory.Store.FailPut = true;
        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(async () => await Router().CommitAsync(await Stage("x"), _tenant, _doc, 1, "txt", default));
        Assert.Contains("Settings > Storage", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, _tenant.ToString("N"))));
    }

    [Fact]
    public async Task A_copy_is_checked_against_the_original_checksum_and_works_in_both_directions()
    {
        var r = Router();
        var local = await r.CommitAsync(await Stage("move me"), _tenant, _doc, 1, "txt", default);   // default: local
        UseCloud();

        await Assert.ThrowsAsync<InvalidDataException>(() => r.CopyAsync(local.Path, "local", "remote", _tenant, Sha("something else"), default));
        Assert.Empty(_factory.Store.Objects);                                                        // a bad copy leaves nothing behind

        var up = await r.CopyAsync(local.Path, "local", "remote", _tenant, Sha("move me"), default);
        Assert.Equal("remote", up.Kind);
        Assert.Equal("move me", Encoding.UTF8.GetString(_factory.Store.Objects["hms/" + local.Path]));
        Assert.Equal("move me", await Read(await r.OpenReadAsync(local.Path, "local", _tenant, default)));   // the source is untouched until the admin removes it

        await r.RemoveAsync(local.Path, "local", _tenant, default);
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await r.OpenReadAsync(local.Path, "local", _tenant, default));
        var back = await r.CopyAsync(local.Path, "remote", "local", _tenant, Sha("move me"), default);
        Assert.Equal("local", back.Kind);
        Assert.Equal("move me", await Read(await r.OpenReadAsync(local.Path, "local", _tenant, default)));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("anything/else.txt")]
    public async Task Paths_outside_the_expected_shape_are_refused(string path)
    {
        UseCloud();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Router().OpenReadAsync(path, "remote", _tenant, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Router().OpenReadAsync(path, "local", _tenant, default));
    }

    [Fact]
    public async Task One_company_can_never_read_another_companys_file()
    {
        var other = Guid.NewGuid();
        var path = $"{other:N}/{_doc:N}/v1/original.txt";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Router().OpenReadAsync(path, "local", _tenant, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Router().RemoveAsync(path, "remote", _tenant, default));
    }

    [Fact]
    public async Task Deleting_a_document_or_company_removes_the_files_from_this_server_and_from_the_bucket()
    {
        UseCloud();
        var r = Router();
        await r.CommitAsync(await Stage("in cloud"), _tenant, _doc, 1, "txt", default);
        _db.StorageSettings.Single().Provider = "local"; _db.SaveChanges();               // switched back: the bucket is still known
        await r.CommitAsync(await Stage("on server"), _tenant, _doc, 2, "txt", default);
        Assert.Single(_factory.Store.Objects);

        await r.DeleteDocumentAsync(_tenant, _doc, default);
        Assert.Empty(_factory.Store.Objects);
        Assert.False(Directory.Exists(Path.Combine(_root, _tenant.ToString("N"), _doc.ToString("N"))));

        await r.CommitAsync(await Stage("again"), _tenant, _doc, 3, "txt", default);
        await r.DeleteTenantAsync(_tenant, default);
        Assert.False(Directory.Exists(Path.Combine(_root, _tenant.ToString("N"))));
    }
}

/// <summary>Settings > Storage over HTTP: validation, secrets, the upload path and moving files.</summary>
public class StorageSettingsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _f;
    private readonly MemoryStoreFactory _cloud = new();
    private const string Account = "0123456789abcdef0123456789abcdef";

    public StorageSettingsApiTests(WebApplicationFactory<Program> factory) => _f = factory.Configure().WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        foreach (var d in s.Where(x => x.ServiceType == typeof(IObjectStoreFactory)).ToList()) s.Remove(d);
        s.AddSingleton<IObjectStoreFactory>(_cloud);
    }));

    private Guid SeedTenant()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var t = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = t, Name = "Acme", Slug = "st-" + t.ToString("N")[..8], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        db.SaveChanges();
        return t;
    }
    private HttpClient Client(Guid tenant, string role = Roles.TenantAdmin)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", role, tenant).Token);
        return c;
    }
    private static object Cloud(string key = "AKIAEXAMPLE1234", string secret = "SECRET-VALUE-123", string bucket = "hms-docs", string? prefix = "acme") =>
        new { provider = "r2", accountId = Account, bucket, prefix, accessKeyId = key, secretAccessKey = secret };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Guid> NewApp(HttpClient admin) =>
        (await Json(await admin.PostAsJsonAsync("/api/v1/applications", new { name = "App" }))).GetProperty("id").GetGuid();

    private static async Task<HttpResponseMessage> Upload(HttpClient admin, Guid app, string name, string text)
    {
        var form = new MultipartFormDataContent { { new StringContent(app.ToString()), "ApplicationId" }, { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "File", name } };
        return await admin.PostAsync("/api/v1/documents", form);
    }

    [Fact]
    public async Task Cloud_storage_is_only_saved_after_it_works_and_its_secret_never_comes_back()
    {
        var t = SeedTenant(); var admin = Client(t);
        var start = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.Equal("local", start.GetProperty("provider").GetString()); Assert.False(start.GetProperty("hasKeys").GetBoolean());

        var bad = await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud(key: "BADKEY-1234"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not recognised", await bad.Content.ReadAsStringAsync());
        Assert.False((await Json(await admin.GetAsync("/api/v1/settings/storage"))).GetProperty("hasKeys").GetBoolean());   // a failed test saves nothing

        var ok = await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-VALUE-123", body); Assert.DoesNotContain("AKIAEXAMPLE1234", body);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal("r2", j.GetProperty("provider").GetString());
        Assert.Equal($"https://{Account}.r2.cloudflarestorage.com", j.GetProperty("endpoint").GetString());
        Assert.Equal(Account, j.GetProperty("accountId").GetString());
        Assert.Equal("1234", j.GetProperty("accessKeyHint").GetString()); Assert.True(j.GetProperty("hasKeys").GetBoolean());
        Assert.True(j.GetProperty("lastTest").GetProperty("ok").GetBoolean());

        using (var scope = _f.Services.CreateScope())   // stored encrypted, not as typed
        {
            var s = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().StorageSettings.IgnoreQueryFilters().Single(x => x.TenantId == t);
            Assert.StartsWith("v1.", s.AccessKeyCipher); Assert.StartsWith("v1.", s.SecretKeyCipher);
            Assert.DoesNotContain("SECRET-VALUE-123", s.SecretKeyCipher); Assert.DoesNotContain("AKIAEXAMPLE1234", s.AccessKeyCipher);
        }

        // changing the bucket with blank keys keeps the saved keys
        var again = await admin.PutAsJsonAsync("/api/v1/settings/storage", new { provider = "r2", accountId = Account, bucket = "another-bucket", prefix = "", accessKeyId = "", secretAccessKey = "" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("AKIAEXAMPLE1234", _cloud.Seen.Last().AccessKey); Assert.Equal("another-bucket", _cloud.Seen.Last().Bucket);
    }

    [Theory]
    [InlineData("s3", "http://storage.example.com", "hms-docs")]            // not https
    [InlineData("s3", "https://localhost:9000", "hms-docs")]                // this server
    [InlineData("s3", "https://192.168.1.20:9000", "hms-docs")]             // private network
    [InlineData("s3", "https://169.254.169.254", "hms-docs")]               // cloud metadata
    [InlineData("s3", "https://203.0.113.9", "Bad_Bucket!")]                // bad bucket name
    [InlineData("s3", "", "hms-docs")]                                      // no address
    public async Task Unsafe_or_incomplete_storage_settings_are_refused_before_anything_is_contacted(string provider, string endpoint, string bucket)
    {
        var t = SeedTenant(); var admin = Client(t);
        var before = _cloud.Seen.Count;
        var r = await admin.PutAsJsonAsync("/api/v1/settings/storage", new { provider, endpoint, bucket, accessKeyId = "AKIAEXAMPLE1234", secretAccessKey = "s" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(before, _cloud.Seen.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/settings/storage/test", new { provider, endpoint, bucket, accessKeyId = "AKIAEXAMPLE1234", secretAccessKey = "s" })).StatusCode);
    }

    [Fact]
    public async Task New_uploads_follow_the_chosen_location_and_existing_files_can_be_copied_each_way()
    {
        var t = SeedTenant(); var admin = Client(t); var app = await NewApp(admin);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(admin, app, "first.txt", "Registration page: the nurse enters the name.")).StatusCode);   // default: this server

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(admin, app, "second.txt", "Billing page: the cashier prints the invoice.")).StatusCode);
        Assert.Single(_cloud.Store.Objects.Keys.Where(k => k.StartsWith("acme/" + t.ToString("N"))));                                               // only the new file is in the bucket

        var view = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.Equal(1, view.GetProperty("local").GetProperty("files").GetInt32()); Assert.Equal(1, view.GetProperty("remote").GetProperty("files").GetInt32());

        var up = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "remote", removeSource = false, take = 10 }));
        Assert.Equal(1, up.GetProperty("moved").GetInt32()); Assert.Equal(0, up.GetProperty("remaining").GetInt32()); Assert.Equal(0, up.GetProperty("failed").GetArrayLength());
        Assert.Equal(2, _cloud.Store.Objects.Keys.Count(k => k.StartsWith("acme/" + t.ToString("N"))));

        var down = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "local", removeSource = true, take = 10 }));
        Assert.Equal(2, down.GetProperty("moved").GetInt32()); Assert.Equal(0, down.GetProperty("remaining").GetInt32());
        Assert.Empty(_cloud.Store.Objects.Keys.Where(k => k.StartsWith("acme/" + t.ToString("N"))));                                               // removed from the bucket after the verified copy
        var end = await Json(await admin.GetAsync("/api/v1/settings/storage"));
        Assert.Equal(2, end.GetProperty("local").GetProperty("files").GetInt32()); Assert.Equal(0, end.GetProperty("remote").GetProperty("files").GetInt32());

        var none = await Json(await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "local", take = 10 }));
        Assert.Equal(0, none.GetProperty("moved").GetInt32());
    }

    [Fact]
    public async Task When_the_cloud_is_down_the_upload_fails_clearly_and_nothing_is_saved_elsewhere()
    {
        var t = SeedTenant(); var admin = Client(t); var app = await NewApp(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud())).StatusCode);
        _cloud.Store.FailPut = true;
        try
        {
            var r = await Upload(admin, app, "down.txt", "Some text for the document.");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("Settings > Storage", await r.Content.ReadAsStringAsync());
            Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/v1/documents")).EnumerateArray());
        }
        finally { _cloud.Store.FailPut = false; }
    }

    [Fact]
    public async Task Migrating_to_the_cloud_needs_saved_cloud_settings_and_switching_back_keeps_them()
    {
        var t = SeedTenant(); var admin = Client(t);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/settings/storage/migrate", new { to = "remote", take = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud())).StatusCode);
        var local = await Json(await admin.PutAsJsonAsync("/api/v1/settings/storage", new { provider = "local" }));
        Assert.Equal("local", local.GetProperty("provider").GetString()); Assert.True(local.GetProperty("hasKeys").GetBoolean());   // connection kept for files already in the bucket
    }

    [Fact]
    public async Task Ordinary_users_are_refused_and_each_company_sees_only_its_own_settings()
    {
        var t1 = SeedTenant(); var t2 = SeedTenant(); var a1 = Client(t1);
        Assert.Equal(HttpStatusCode.OK, (await a1.PutAsJsonAsync("/api/v1/settings/storage", Cloud())).StatusCode);
        Assert.Equal("local", (await Json(await Client(t2).GetAsync("/api/v1/settings/storage"))).GetProperty("provider").GetString());
        // an ordinary user is a real, active access key (the server checks that on every request)\n        var keyId = Guid.NewGuid();\n        using (var scope = _f.Services.CreateScope())\n        {\n            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();\n            db.AccessKeys.Add(new TenantAccessKey { Id = keyId, TenantId = t1, Name = "k", KeyPrefix = "fm_x", KeyHash = ApiKeys.Hash(keyId.ToString()), IsActive = true }); db.SaveChanges();\n        }\n        var user = _f.CreateClient();\n        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue($"key:{keyId:N}", Roles.TenantUser, t1).Token);\n        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/settings/storage")).StatusCode);
    }

    [Fact]
    public async Task The_overview_activity_and_security_pages_describe_the_company_and_record_changes()
    {
        var t = SeedTenant(); var admin = Client(t);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/storage", Cloud())).StatusCode);

        var o = await Json(await admin.GetAsync("/api/v1/settings/overview"));
        Assert.True(o.GetProperty("locations").GetArrayLength() >= 4);
        Assert.Contains("Cloudflare R2", o.GetProperty("locations")[0].GetProperty("where").GetString());

        var act = await Json(await admin.GetAsync("/api/v1/settings/activity"));
        Assert.Contains(act.EnumerateArray(), a => a.GetProperty("action").GetString() == "settings.storage");
        Assert.DoesNotContain("SECRET-VALUE-123", act.ToString());

        var sec = await Json(await admin.GetAsync("/api/v1/settings/security"));
        Assert.Contains(sec.EnumerateArray(), c => c.GetProperty("name").GetString() == "Cloud storage credentials" && c.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("SECRET-VALUE-123", sec.ToString());
    }
}
