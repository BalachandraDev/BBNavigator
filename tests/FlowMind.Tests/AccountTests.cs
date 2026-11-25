using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowMind.API.Authentication;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Accounts;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FlowMind.Tests;

/// <summary>Real hashing is deliberately slow; the whole suite uses a low iteration count so it stays fast. The hash format carries its own count.</summary>
internal static class FastPasswords
{
    [ModuleInitializer] internal static void Init() => PasswordHasher.Iterations = 1200;
}

public class PasswordHasherTests
{
    [Fact]
    public void A_password_verifies_only_against_its_own_hash_and_every_hash_has_its_own_salt()
    {
        var a = PasswordHasher.Hash("correct horse battery"); var b = PasswordHasher.Hash("correct horse battery");
        Assert.NotEqual(a, b);                                                       // random salt
        Assert.StartsWith("v1$", a); Assert.DoesNotContain("correct", a);            // the password itself is never in what is stored
        Assert.True(PasswordHasher.Verify("correct horse battery", a, out _));
        Assert.False(PasswordHasher.Verify("correct horse batterY", a, out _));
        Assert.False(PasswordHasher.Verify("", a, out _));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("plain text")] [InlineData("v1$abc$def$ghi")] [InlineData("v1$1200$!!!$???")] [InlineData("v2$1200$AAAA$AAAA")] [InlineData("v1$1$AAAA$AAAA")]
    public void Garbage_is_never_a_valid_hash_and_never_throws(string? stored) => Assert.False(PasswordHasher.Verify("anything", stored, out _));

    [Fact]
    public void A_hash_made_with_fewer_iterations_still_verifies_and_asks_to_be_upgraded()
    {
        var old = PasswordHasher.Iterations; string h;
        try { PasswordHasher.Iterations = 1000; h = PasswordHasher.Hash("a long enough password"); } finally { PasswordHasher.Iterations = old; }
        Assert.True(PasswordHasher.Verify("a long enough password", h, out var rehash));
        Assert.True(rehash);
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1", "Use at least")]
    [InlineData("aaaaaaaaaaaa", "repeats")]
    [InlineData("Password123", "too common")]
    [InlineData("priya.nair@example.com", "e-mail")]
    [InlineData("xxpriya.nairxx99", "e-mail")]
    public void Weak_passwords_are_refused_with_the_reason(string password, string reasonContains) =>
        Assert.Contains(reasonContains, PasswordPolicy.Check(password, "priya.nair@example.com", "Priya Kumar")!, StringComparison.OrdinalIgnoreCase);

    [Fact] public void The_name_may_not_be_the_password() => Assert.Contains("name", PasswordPolicy.Check("my-kumar-secret-1", "x@y.com", "Priya Kumar")!);
    [Fact] public void Long_unrelated_words_are_fine_without_symbols_or_capitals() => Assert.Null(PasswordPolicy.Check("blue lamp river stone", "priya@example.com", "Priya Nair"));
    [Fact] public void Absurdly_long_passwords_are_refused() => Assert.Contains("at most", PasswordPolicy.Check(new string('a', 100) + "bcdefg" + new string('h', 30), null, null)!);
}

/// <summary>Records instead of sending, so a test can follow the link in an invitation or reset e-mail.</summary>
public sealed class CapturingMail : IEmailSender
{
    public bool Enabled { get; set; } = true;
    public List<(string To, string Subject, string Body)> Sent { get; } = new();
    public Task<bool> SendAsync(string to, string subject, string textBody, CancellationToken ct) { lock (Sent) Sent.Add((to, subject, textBody)); return Task.FromResult(true); }
    public string LastToken(string to)
    {
        lock (Sent) { var m = Sent.LastOrDefault(s => s.To == to); return Regex.Match(m.Body ?? "", @"token=([A-Za-z0-9_\-%]+)").Groups[1].Value; }
    }
}

public class AccountApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class NoVectors : FlowMind.Application.Interfaces.IVectorStore
    {
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<FlowMind.Application.Interfaces.ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FlowMind.Application.Interfaces.ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) => Task.FromResult<IReadOnlyList<FlowMind.Application.Interfaces.ChunkHit>>(Array.Empty<FlowMind.Application.Interfaces.ChunkHit>());
    }

    private const string Good = "blue lamp river stone";
    private readonly WebApplicationFactory<Program> _f;
    private readonly CapturingMail _mail = new() { Enabled = false };

    public AccountApiTests(WebApplicationFactory<Program> factory) =>
        _f = factory.Configure().WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:AuthRequestsPerMinute", "100000");
            b.ConfigureServices(s => { s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(_mail); s.RemoveAll<FlowMind.Application.Interfaces.IVectorStore>(); s.AddSingleton<FlowMind.Application.Interfaces.IVectorStore>(new NoVectors()); });
        });

    private HttpClient Anonymous() => _f.CreateClient();
    private HttpClient Bearer(string token) { var c = _f.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private HttpClient Platform() => Bearer(_f.Services.GetRequiredService<TokenService>().Issue("platform-admin", Roles.PlatformAdmin, null).Token);
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static string Email(string tag) => $"{tag}-{Guid.NewGuid().ToString("N")[..6]}@example.com";

    /// <summary>A company whose first admin was invited by the platform admin and chose a password. Returns the admin's session.</summary>
    private async Task<(Guid TenantId, string AdminEmail, string AdminToken)> NewCompany(string? adminEmail = null)
    {
        var email = adminEmail ?? Email("admin"); var slug = "co-" + Guid.NewGuid().ToString("N")[..8];
        var created = await Platform().PostAsJsonAsync("/api/v1/tenants", new { name = "Acme Hospital", slug, adminEmail = email, adminName = "Asha Admin" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var j = await Json(created);
        var token = Uri.UnescapeDataString(Regex.Match(j.GetProperty("inviteLink").GetString()!, "token=(.+)$").Groups[1].Value);
        var session = await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = Good }));
        return (j.GetProperty("id").GetGuid(), email, session.GetProperty("accessToken").GetString()!);
    }

    private async Task<string> InviteAndAccept(HttpClient admin, string email, string name, string role = "TenantUser", Guid? audience = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email, name, role, audienceId = audience });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = Uri.UnescapeDataString(Regex.Match((await Json(r)).GetProperty("link").GetString()!, "token=(.+)$").Groups[1].Value);
        var s = await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = Good }));
        return s.GetProperty("accessToken").GetString()!;
    }

    // ---- sign in ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_platform_admin_invites_a_company_admin_who_chooses_a_password_and_signs_in_with_the_email_alone()
    {
        var (tenant, email, session) = await NewCompany();
        var me = await Json(await Bearer(session).GetAsync("/api/v1/account/me"));
        Assert.Equal("account", me.GetProperty("kind").GetString());
        Assert.Equal(email, me.GetProperty("profile").GetProperty("email").GetString());
        Assert.Equal("TenantAdmin", me.GetProperty("profile").GetProperty("role").GetString());

        var login = await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email = email.ToUpperInvariant(), password = Good });   // the e-mail is not case sensitive
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await Json(login)).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/v1/applications")).StatusCode);   // a real company admin session
        using var scope = _f.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().UserAccounts.Single(u => u.Email == email).LastLoginUtc);
        Assert.Equal(tenant, scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().UserAccounts.Single(u => u.Email == email).TenantId);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_get_exactly_the_same_answer()
    {
        var (_, email, _) = await NewCompany();
        var wrong = await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = "not the password at all" });
        var unknown = await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email = Email("nobody"), password = "not the password at all" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal((await Json(wrong)).GetProperty("detail").GetString(), (await Json(unknown)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_account_locks_after_repeated_wrong_passwords_and_the_right_password_is_refused_while_locked()
    {
        var (_, email, _) = await NewCompany();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong wrong wrong" + i })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);
        using (var scope = _f.Services.CreateScope())                                // the lock ends by itself
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>(); db.UserAccounts.Single(u => u.Email == email).LockedUntilUtc = DateTime.UtcNow.AddSeconds(-1); db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);
    }

    [Fact]
    public async Task A_pending_invitation_cannot_sign_in_and_a_switched_off_company_is_refused_after_the_right_password()
    {
        var (tenant, email, _) = await NewCompany();
        var pending = Email("pending");
        Assert.Equal(HttpStatusCode.OK, (await Bearer((await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good }))).GetProperty("accessToken").GetString()!).PostAsJsonAsync("/api/v1/users", new { email = pending, name = "Pat Pending" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email = pending, password = Good })).StatusCode);   // no password chosen yet
        Assert.Equal(HttpStatusCode.NoContent, (await Platform().PostAsync($"/api/v1/tenants/{tenant}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);
    }

    // ---- invitations, forgot password, reset -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invitation_link_works_once_names_the_person_and_refuses_a_weak_password()
    {
        var admin = Bearer((await NewCompany()).AdminToken); var email = Email("nurse");
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Nina Nurse", role = "TenantUser" });
        var link = (await Json(r)).GetProperty("link").GetString()!;
        var token = Uri.UnescapeDataString(Regex.Match(link, "token=(.+)$").Groups[1].Value);

        var info = await Json(await Anonymous().GetAsync("/api/v1/auth/token-info?token=" + Uri.EscapeDataString(token)));
        Assert.Equal(email, info.GetProperty("email").GetString()); Assert.Equal("invite", info.GetProperty("purpose").GetString());
        var weak = await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = "password123" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode); Assert.Contains("common", await weak.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = Good })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = Good + " again" })).StatusCode);   // single use
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync("/api/v1/auth/token-info?token=" + Uri.EscapeDataString(token))).StatusCode);
    }

    [Fact]
    public async Task Forgot_password_answers_alike_for_everyone_and_the_emailed_link_resets_it_once_and_ends_old_sessions()
    {
        var (_, email, oldSession) = await NewCompany();
        _mail.Enabled = true;
        try
        {
            var known = await Anonymous().PostAsJsonAsync("/api/v1/auth/forgot", new { email });
            var unknown = await Anonymous().PostAsJsonAsync("/api/v1/auth/forgot", new { email = Email("ghost") });
            Assert.Equal(HttpStatusCode.Accepted, known.StatusCode); Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
            Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());   // nothing tells the two apart

            await Task.Delay(300);                                                    // the e-mail is sent in the background
            var token = Uri.UnescapeDataString(_mail.LastToken(email));
            Assert.NotEmpty(token);
            Assert.DoesNotContain(_mail.Sent, s => s.To.StartsWith("ghost"));        // nothing was sent to an address without an account

            var again = await Anonymous().PostAsJsonAsync("/api/v1/auth/forgot", new { email });   // within a minute: no second e-mail
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode); await Task.Delay(200);
            Assert.Single(_mail.Sent, s => s.To == email);

            var fresh = "green desk window sun";
            Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = fresh })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = fresh })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);   // the old password is gone
            Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = fresh })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(oldSession).GetAsync("/api/v1/applications")).StatusCode);                              // so is the old session
        }
        finally { _mail.Enabled = false; }
    }

    [Fact]
    public async Task Without_a_mail_server_forgot_password_sends_nothing_and_the_admin_gets_a_link_to_pass_on()
    {
        var admin = Bearer((await NewCompany()).AdminToken); var email = Email("clerk");
        var token = await InviteAndAccept(admin, email, "Cleo Clerk");
        _mail.Enabled = false;
        var before = _mail.Sent.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await Anonymous().PostAsJsonAsync("/api/v1/auth/forgot", new { email })).StatusCode);
        await Task.Delay(200); Assert.Equal(before, _mail.Sent.Count);
        var id = (await Json(await admin.GetAsync("/api/v1/users"))).EnumerateArray().Single(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
        var reset = await Json(await admin.PostAsync($"/api/v1/users/{id}/reset", null));
        Assert.False(reset.GetProperty("emailSent").GetBoolean());
        Assert.Contains("#/reset?token=", reset.GetProperty("link").GetString());
        Assert.NotEmpty(token);
    }

    // ---- my account ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_follows_the_policy_and_ends_other_sessions()
    {
        var (_, email, session) = await NewCompany();
        var other = (await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good }))).GetProperty("accessToken").GetString()!;   // a second session
        var me = Bearer(session);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/account/password", new { current = "not it at all", next = "green desk window sun" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/account/password", new { current = Good, next = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/account/password", new { current = Good, next = Good })).StatusCode);   // must actually change
        var ok = await me.PostAsJsonAsync("/api/v1/account/password", new { current = Good, next = "green desk window sun" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var fresh = (await Json(ok)).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Bearer(fresh).GetAsync("/api/v1/applications")).StatusCode);             // this session carries on
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(other).GetAsync("/api/v1/applications")).StatusCode);   // the other one ended
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_session_and_a_person_can_rename_themselves()
    {
        var (_, email, session) = await NewCompany();
        var other = (await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good }))).GetProperty("accessToken").GetString()!;
        var renamed = await Bearer(session).PutAsJsonAsync("/api/v1/account/me", new { displayName = "Asha K" });
        Assert.Equal("Asha K", (await Json(renamed)).GetProperty("profile").GetProperty("displayName").GetString());
        var out1 = await Bearer(session).PostAsync("/api/v1/account/sign-out-everywhere", null);
        Assert.Equal(HttpStatusCode.OK, out1.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(other).GetAsync("/api/v1/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(session).GetAsync("/api/v1/applications")).StatusCode);   // including the old token of this one
    }

    [Fact]
    public async Task A_person_signed_in_with_a_key_has_no_account_page_and_is_told_so()
    {
        var token = _f.Services.GetRequiredService<TokenService>().Issue("tenant:legacy", Roles.TenantAdmin, Guid.NewGuid()).Token;
        using (var scope = _f.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>(); }
        var me = Bearer(token);
        // (the key session is not checked against accounts; only its tenant must be active, so make one)
        var t = Guid.NewGuid();
        using (var scope = _f.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>(); db.Tenants.Add(new Tenant { Id = t, Name = "Keys Co", Slug = "keys-" + t.ToString("N")[..6], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) }); db.SaveChanges(); }
        var keySession = Bearer(_f.Services.GetRequiredService<TokenService>().Issue("tenant:keys", Roles.TenantAdmin, t).Token);
        Assert.Equal("key", (await Json(await keySession.GetAsync("/api/v1/account/me"))).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await keySession.PostAsJsonAsync("/api/v1/account/password", new { current = "x", next = "y" })).StatusCode);
        Assert.NotNull(me);
    }

    // ---- the team page -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admins_manage_only_their_own_company_and_ordinary_users_cannot_manage_anyone()
    {
        var a = await NewCompany(); var b = await NewCompany();
        var adminA = Bearer(a.AdminToken); var adminB = Bearer(b.AdminToken);
        var userEmail = Email("user"); var userToken = await InviteAndAccept(adminA, userEmail, "Uma User");

        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(userToken).GetAsync("/api/v1/users")).StatusCode);                 // a user cannot list the team
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(userToken).PostAsJsonAsync("/api/v1/users", new { email = Email("x"), name = "Xavier X" })).StatusCode);
        var rows = (await Json(await adminB.GetAsync("/api/v1/users"))).EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.DoesNotContain(userEmail, rows);                                                                                 // company B never sees company A's people
        var id = (await Json(await adminA.GetAsync("/api/v1/users"))).EnumerateArray().Single(u => u.GetProperty("email").GetString() == userEmail).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PatchAsJsonAsync($"/api/v1/users/{id}", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/v1/users/{id}/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/v1/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminB.PostAsJsonAsync("/api/v1/users", new { email = userEmail, name = "Copycat" })).StatusCode);   // an e-mail belongs to one account on the whole server
    }

    [Fact]
    public async Task Switching_someone_off_ends_their_session_and_a_company_always_keeps_one_active_admin()
    {
        var (_, adminEmail, adminToken) = await NewCompany(); var admin = Bearer(adminToken);
        var email = Email("temp"); var token = await InviteAndAccept(admin, email, "Tim Temp");
        var list = (await Json(await admin.GetAsync("/api/v1/users"))).EnumerateArray().ToList();
        var tim = list.Single(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
        var me = list.Single(u => u.GetProperty("email").GetString() == adminEmail).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/v1/me/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/users/{tim}", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(token).GetAsync("/api/v1/me/access")).StatusCode);                     // immediately
        Assert.Equal(HttpStatusCode.Forbidden, (await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email, password = Good })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/v1/users/{me}", new { active = false })).StatusCode);   // not yourself
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/v1/users/{me}", new { role = "TenantUser" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/users/{me}")).StatusCode);

        // a second admin may demote the first, but nobody may remove the last one
        var second = Email("second"); var secondToken = await InviteAndAccept(admin, second, "Sam Second", "TenantAdmin");
        var secondId = (await Json(await admin.GetAsync("/api/v1/users"))).EnumerateArray().Single(u => u.GetProperty("email").GetString() == second).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Bearer(secondToken).PatchAsJsonAsync($"/api/v1/users/{me}", new { role = "TenantUser" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/applications")).StatusCode);                          // the demoted admin's old session ended with it
        Assert.Equal(HttpStatusCode.Conflict, (await Bearer(secondToken).DeleteAsync($"/api/v1/users/{secondId}")).StatusCode);
        Assert.NotEmpty(adminEmail);
    }

    [Fact]
    public async Task A_user_with_an_audience_gets_that_audiences_scope_and_chat_only_by_default()
    {
        var (tenant, _, adminToken) = await NewCompany(); var admin = Bearer(adminToken);
        var aud = (await Json(await admin.PostAsJsonAsync("/api/v1/access/audiences", new { name = "Ward", aliases = "", description = "", canBrowseKnowledge = false }))).GetProperty("id").GetGuid();
        var email = Email("ward");
        var token = await InviteAndAccept(admin, email, "Wendy Ward", "TenantUser", aud);
        var access = await Json(await Bearer(token).GetAsync("/api/v1/me/access"));
        Assert.False(access.GetProperty("canBrowseKnowledge").GetBoolean());
        Assert.Equal("Ward", access.GetProperty("audience").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(token).GetAsync("/api/v1/modules?applicationId=" + Guid.NewGuid())).StatusCode);   // chat only: no Knowledge page

        var id = (await Json(await admin.GetAsync("/api/v1/users"))).EnumerateArray().Single(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/users/{id}", new { fullAccess = true })).StatusCode);   // back to everything approved
        Assert.True((await Json(await Bearer(token).GetAsync("/api/v1/me/access"))).GetProperty("canBrowseKnowledge").GetBoolean());
        Assert.NotEqual(Guid.Empty, tenant);
    }

    [Fact]
    public async Task Deleting_a_company_removes_its_people_and_frees_their_emails()
    {
        var (tenant, email, _) = await NewCompany();
        Assert.Equal(HttpStatusCode.NoContent, (await Platform().DeleteAsync($"/api/v1/tenants/{tenant}?confirm=" + SlugOf(tenant))).StatusCode);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        Assert.False(db.UserAccounts.Any(u => u.Email == email)); Assert.False(db.AccountTokens.Any(t => !db.UserAccounts.Any(u => u.Id == t.UserId)));
        Assert.Equal(HttpStatusCode.Created, (await Platform().PostAsJsonAsync("/api/v1/tenants", new { name = "Again", slug = "again-" + Guid.NewGuid().ToString("N")[..6], adminEmail = email, adminName = "Asha Admin" })).StatusCode);
    }
    private string SlugOf(Guid tenant) { using var s = _f.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<FlowMindDbContext>().Tenants.IgnoreQueryFilters().Single(t => t.Id == tenant).Slug; }

    [Fact]
    public async Task A_taken_admin_email_creates_no_company_and_a_typo_is_refused_up_front()
    {
        var (_, email, _) = await NewCompany();
        var slug = "dup-" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.Conflict, (await Platform().PostAsJsonAsync("/api/v1/tenants", new { name = "Dup", slug, adminEmail = email, adminName = "Dup Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Platform().PostAsJsonAsync("/api/v1/tenants", new { name = "Typo", slug = slug + "x", adminEmail = "not-an-email", adminName = "T" })).StatusCode);
        using var scope = _f.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().Tenants.IgnoreQueryFilters().Any(t => t.Slug == slug || t.Slug == slug + "x"));
    }

    [Fact]
    public async Task Platform_admins_can_have_email_accounts_too_and_those_open_the_companies_page()
    {
        var email = Email("platform");
        var invited = await Json(await Platform().PostAsJsonAsync("/api/v1/platform-admins", new { email, name = "Pia Platform", role = "PlatformAdmin" }));
        var token = Uri.UnescapeDataString(Regex.Match(invited.GetProperty("link").GetString()!, "token=(.+)$").Groups[1].Value);
        var session = (await Json(await Anonymous().PostAsJsonAsync("/api/v1/auth/reset", new { token, password = Good }))).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Bearer(session).GetAsync("/api/v1/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(session).GetAsync("/api/v1/users")).StatusCode);   // platform admins do not manage a company's people from here
    }

    [Fact]
    public async Task The_sign_in_page_learns_whether_email_is_set_up_without_any_secret()
    {
        var o = await Json(await Anonymous().GetAsync("/api/v1/auth/options"));
        Assert.False(o.GetProperty("emailEnabled").GetBoolean());
        Assert.Equal(10, o.GetProperty("minPasswordLength").GetInt32());
    }
}
