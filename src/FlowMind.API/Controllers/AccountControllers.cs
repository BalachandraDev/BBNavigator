using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Access;
using FlowMind.Infrastructure.Accounts;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed record AccountProfile(Guid Id, string Email, string DisplayName, string Role, string? CompanyName, string? CompanyId, string? Audience, DateTime? LastLoginUtc, DateTime? PasswordChangedUtc);
public sealed record SignInResponse(string AccessToken, string TokenType, DateTime ExpiresUtc, AccountProfile Profile);
public sealed class LoginRequest { [Required, MaxLength(254)] public string Email { get; set; } = ""; [Required, MaxLength(200)] public string Password { get; set; } = ""; }
public sealed class ForgotRequest { [Required, MaxLength(254)] public string Email { get; set; } = ""; }
public sealed class ResetRequest { [Required, MaxLength(200)] public string Token { get; set; } = ""; [Required, MaxLength(200)] public string Password { get; set; } = ""; }

/// <summary>Shared by the controllers below: building a profile and a session for an account.</summary>
internal static class AccountSupport
{
    public static async Task<AccountProfile> ProfileAsync(FlowMindDbContext db, UserAccount u, CancellationToken ct)
    {
        string? company = null, slug = null, audience = null;
        if (u.TenantId is { } t) { var tn = await db.Tenants.Where(x => x.Id == t).Select(x => new { x.Name, x.Slug }).FirstOrDefaultAsync(ct); company = tn?.Name; slug = tn?.Slug; }
        if (u.AudienceId is { } a) audience = await db.Audiences.IgnoreQueryFilters().Where(x => x.Id == a && x.TenantId == u.TenantId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        return new AccountProfile(u.Id, u.Email, u.DisplayName, u.Role, company, slug, audience, u.LastLoginUtc, u.PasswordChangedUtc);
    }

    public static async Task<SignInResponse> SessionAsync(FlowMindDbContext db, TokenService tokens, UserAccount u, CancellationToken ct)
    {
        var t = tokens.Issue($"user:{u.Id:N}", u.Role, u.TenantId, u.SecurityStamp.ToString("N"), u.Email, u.DisplayName);
        return new SignInResponse(t.Token, "Bearer", t.ExpiresUtc, await ProfileAsync(db, u, ct));
    }

    public static Guid? AccountId(ClaimsPrincipal user) =>
        (user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "") is { } s && s.StartsWith("user:") && Guid.TryParseExact(s[5..], "N", out var id) ? id : null;

    public static string BaseUrl(HttpRequest r) => $"{r.Scheme}://{r.Host}";
}

/// <summary>Public sign-in endpoints: e-mail and password, "forgot password", choosing a password from an invitation or reset link.</summary>
[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public class SignInController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly AccountService _accounts;
    private readonly AccountMailer _mailer;
    private readonly TokenService _tokens;
    private readonly IAuditLogger _audit;
    private readonly IServiceScopeFactory _scopes;

    public SignInController(FlowMindDbContext db, AccountService accounts, AccountMailer mailer, TokenService tokens, IAuditLogger audit, IServiceScopeFactory scopes)
    { _db = db; _accounts = accounts; _mailer = mailer; _tokens = tokens; _audit = audit; _scopes = scopes; }

    /// <summary>What the sign-in page may offer. E-mail being set up decides whether "Forgot password?" can send anything.</summary>
    [HttpGet("options")]
    public object Options() => new { emailEnabled = _mailer.Enabled, minPasswordLength = PasswordPolicy.MinLength };

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req, CancellationToken ct)
    {
        var r = await _accounts.LoginAsync(req.Email, req.Password, ct);
        switch (r.Outcome)
        {
            case LoginOutcome.Ok:
                await _audit.LogAsync("auth.login", "User", r.User!.Id.ToString(), r.User.Email, r.User.TenantId, ct);
                return Ok(await AccountSupport.SessionAsync(_db, _tokens, r.User!, ct));
            case LoginOutcome.Locked:
                return Problem("Too many wrong attempts. Wait a few minutes, or reset your password.", statusCode: 429);
            case LoginOutcome.Disabled:
                return Problem("This account is switched off. Ask your administrator.", statusCode: 403);
            default:
                return Problem("That e-mail and password do not match.", statusCode: 401);   // never says which of the two was wrong
        }
    }

    /// <summary>Always answers the same way, whether or not the e-mail has an account, so the page cannot be used to find out who has one.</summary>
    [HttpPost("forgot")]
    public async Task<IActionResult> Forgot(ForgotRequest req, CancellationToken ct)
    {
        var u = await _accounts.FindActiveByEmailAsync(req.Email, ct);
        if (u != null && u.PasswordHash != null && _mailer.Enabled && !await RecentResetAsync(u.Id, ct))
        {
            var token = await _accounts.NewTokenAsync(u, AccountService.Reset, TimeSpan.FromMinutes(_accounts.Options.ResetMinutes), ct);
            var link = _mailer.Link(AccountSupport.BaseUrl(Request), "reset", token);
            _ = Task.Run(async () => { using var scope = _scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<AccountMailer>().SendResetAsync(u, link, CancellationToken.None); });   // sending is slow; never make the answer depend on it
            await _audit.LogAsync("auth.reset-requested", "User", u.Id.ToString(), u.Email, u.TenantId, ct);
        }
        return Accepted(new { message = "If that e-mail has an account, a link to choose a new password is on its way." });
    }

    /// <summary>One reset e-mail per minute per account, so the page cannot be used to flood someone's inbox.</summary>
    private Task<bool> RecentResetAsync(Guid userId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMinutes(-1);
        return _db.AccountTokens.AnyAsync(t => t.UserId == userId && t.Purpose == AccountService.Reset && t.CreatedUtc > since, ct);
    }

    /// <summary>Whose password a link sets, so the page can say "Choose a password for ...". 404 when the link is unknown, used or expired.</summary>
    [HttpGet("token-info")]
    public async Task<IActionResult> TokenInfo([FromQuery] string? token, CancellationToken ct)
    {
        var info = await _accounts.PeekAsync(token, ct);
        return info == null ? Problem("This link has expired or was already used. Ask for a new one.", statusCode: 404) : Ok(info);
    }

    /// <summary>Choose a password from an invitation or a reset link. The person is signed in straight away.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(ResetRequest req, CancellationToken ct)
    {
        try
        {
            var u = await _accounts.RedeemAsync(req.Token, req.Password, ct);
            await _audit.LogAsync("auth.password-set", "User", u.Id.ToString(), u.Email, u.TenantId, ct);
            u.LastLoginUtc = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
            return Ok(await AccountSupport.SessionAsync(_db, _tokens, u, ct));
        }
        catch (AccountException ex) { return Problem(ex.Message, statusCode: ex.Status); }
    }
}

public sealed class UpdateProfileRequest { [Required, StringLength(100, MinimumLength = 2)] public string DisplayName { get; set; } = ""; }
public sealed class ChangePasswordRequest { [Required, MaxLength(200)] public string Current { get; set; } = ""; [Required, MaxLength(200)] public string Next { get; set; } = ""; }

/// <summary>"My account": the signed-in person's own profile and password.</summary>
[ApiController]
[Route("api/v1/account")]
[Authorize]   // any signed-in person; the endpoints themselves decide what a key session (no account) gets
public class AccountController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly AccountService _accounts;
    private readonly TokenService _tokens;
    private readonly TokenStatusChecker _status;
    private readonly IAuditLogger _audit;

    public AccountController(FlowMindDbContext db, AccountService accounts, TokenService tokens, TokenStatusChecker status, IAuditLogger audit)
    { _db = db; _accounts = accounts; _tokens = tokens; _status = status; _audit = audit; }

    private async Task<UserAccount?> MeAsync(CancellationToken ct) =>
        AccountSupport.AccountId(User) is { } id ? await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, ct) : null;

    /// <summary>The signed-in person. People using a company or user KEY have no account: they get kind "key" and the page explains it.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var u = await MeAsync(ct);
        if (u == null) return Ok(new { kind = "key", role = User.FindFirstValue(ClaimNames.Role) });
        return Ok(new { kind = "account", profile = await AccountSupport.ProfileAsync(_db, u, ct) });
    }

    [HttpPut("me")]
    public async Task<IActionResult> Update(UpdateProfileRequest req, CancellationToken ct)
    {
        var u = await MeAsync(ct); if (u == null) return NotFound();
        u.DisplayName = req.DisplayName.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(await AccountSupport.SessionAsync(_db, _tokens, u, ct));   // the name is in the session, so hand back a fresh one
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        var u = await MeAsync(ct); if (u == null) return NotFound();
        try { await _accounts.ChangePasswordAsync(u, req.Current, req.Next, ct); }
        catch (AccountException ex) { return Problem(ex.Message, statusCode: ex.Status); }
        _status.Forget(u.Id);
        await _audit.LogAsync("account.password-change", "User", u.Id.ToString(), null, u.TenantId, ct);
        return Ok(await AccountSupport.SessionAsync(_db, _tokens, u, ct));   // every other session ended; this one carries on with a fresh token
    }

    [HttpPost("sign-out-everywhere")]
    public async Task<IActionResult> SignOutEverywhere(CancellationToken ct)
    {
        var u = await MeAsync(ct); if (u == null) return NotFound();
        await _accounts.EndSessionsAsync(u, ct);
        _status.Forget(u.Id);
        await _audit.LogAsync("account.sign-out-everywhere", "User", u.Id.ToString(), null, u.TenantId, ct);
        return Ok(await AccountSupport.SessionAsync(_db, _tokens, u, ct));
    }
}

public sealed record TeamMember(Guid Id, string Email, string DisplayName, string Role, Guid? AudienceId, string? Audience, bool Active, bool PendingInvite, bool Locked, DateTime CreatedUtc, DateTime? LastLoginUtc);
public sealed record InviteResult(TeamMember Member, bool EmailSent, string? Link);
public sealed class InviteRequest
{
    [Required, MaxLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 2)] public string Name { get; set; } = "";
    /// <summary>"TenantAdmin" (manages everything) or "TenantUser" (chat, and the Knowledge page if their audience allows it).</summary>
    public string Role { get; set; } = "TenantUser";
    public Guid? AudienceId { get; set; }
}
public sealed class UpdateMemberRequest
{
    [StringLength(100, MinimumLength = 2)] public string? Name { get; set; }
    public string? Role { get; set; }
    public Guid? AudienceId { get; set; }
    /// <summary>True = remove the audience (full access).</summary>
    public bool FullAccess { get; set; }
    public bool? Active { get; set; }
}

/// <summary>Company admins: the people of this company, who may sign in, with which role, and resetting their passwords.</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "TenantAdmin")]
public class UsersController : ControllerBase
{
    private const int MaxUsers = 500;
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly AccountService _accounts;
    private readonly AccountMailer _mailer;
    private readonly TokenStatusChecker _status;
    private readonly IAuditLogger _audit;

    public UsersController(FlowMindDbContext db, ITenantContext tenant, AccountService accounts, AccountMailer mailer, TokenStatusChecker status, IAuditLogger audit)
    { _db = db; _tenant = tenant; _accounts = accounts; _mailer = mailer; _status = status; _audit = audit; }

    private Guid TenantId => _tenant.TenantId!.Value;
    private IQueryable<UserAccount> Mine => _db.UserAccounts.Where(u => u.TenantId == TenantId);   // always this company only

    private async Task<TeamMember> ToMember(UserAccount u, CancellationToken ct) => new(u.Id, u.Email, u.DisplayName, u.Role, u.AudienceId,
        u.AudienceId == null ? null : await _db.Audiences.Where(a => a.Id == u.AudienceId).Select(a => a.Name).FirstOrDefaultAsync(ct),
        u.IsActive, u.PasswordHash == null, u.LockedUntilUtc > DateTime.UtcNow, u.CreatedUtc, u.LastLoginUtc);

    [HttpGet]
    public async Task<IReadOnlyList<TeamMember>> List(CancellationToken ct)
    {
        var audiences = await _db.Audiences.ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return (await Mine.OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(u => new TeamMember(u.Id, u.Email, u.DisplayName, u.Role, u.AudienceId,
            u.AudienceId is { } a && audiences.TryGetValue(a, out var n) ? n : null, u.IsActive, u.PasswordHash == null, u.LockedUntilUtc > DateTime.UtcNow, u.CreatedUtc, u.LastLoginUtc)).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Invite(InviteRequest req, CancellationToken ct)
    {
        if (req.Role is not (Roles.TenantAdmin or Roles.TenantUser)) return Problem("Choose Admin or User.", statusCode: 400);
        if (await Mine.CountAsync(ct) >= MaxUsers) return Problem($"A company can have up to {MaxUsers} people.", statusCode: 409);
        if (req.AudienceId is { } a && !await _db.Audiences.AnyAsync(x => x.Id == a, ct)) return Problem("That audience does not exist.", statusCode: 400);
        try
        {
            var (u, token) = await _accounts.InviteAsync(TenantId, req.Email, req.Name, req.Role, req.AudienceId, ct);
            AccessResolver.Invalidate(TenantId);
            var company = await _db.Tenants.Where(t => t.Id == TenantId).Select(t => t.Name).FirstAsync(ct);
            var link = _mailer.Link(AccountSupport.BaseUrl(Request), "invite", token);
            var sent = _mailer.Enabled && await _mailer.SendInviteAsync(u, company, link, ct);
            await _audit.LogAsync("user.invite", "User", u.Id.ToString(), $"{u.Email} as {u.Role}", ct: ct);
            return Ok(new InviteResult(await ToMember(u, ct), sent, sent ? null : link));   // no e-mail sent: the admin passes the link on
        }
        catch (AccountException ex) { return Problem(ex.Message, statusCode: ex.Status); }
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateMemberRequest req, CancellationToken ct)
    {
        var u = await Mine.FirstOrDefaultAsync(x => x.Id == id, ct); if (u == null) return NotFound();
        var self = AccountSupport.AccountId(User) == u.Id;
        var wasAdmin = u.Role == Roles.TenantAdmin && u.IsActive;
        if (req.Role != null && req.Role != u.Role)
        {
            if (req.Role is not (Roles.TenantAdmin or Roles.TenantUser)) return Problem("Choose Admin or User.", statusCode: 400);
            if (self) return Problem("You cannot change your own role.", statusCode: 409);
            u.Role = req.Role; if (u.Role == Roles.TenantAdmin) u.AudienceId = null;
            await _accounts.EndSessionsAsync(u, ct);          // a changed role takes effect now, not at the next sign-in
        }
        if (req.FullAccess) u.AudienceId = null;
        else if (req.AudienceId is { } a)
        {
            if (!await _db.Audiences.AnyAsync(x => x.Id == a, ct)) return Problem("That audience does not exist.", statusCode: 400);
            if (u.Role == Roles.TenantUser) u.AudienceId = a;
        }
        if (req.Name != null) u.DisplayName = req.Name.Trim();
        if (req.Active is { } on && on != u.IsActive)
        {
            if (self) return Problem("You cannot switch off your own account.", statusCode: 409);
            u.IsActive = on;
            if (!on) await _accounts.EndSessionsAsync(u, ct);
        }
        if (wasAdmin && !(u.Role == Roles.TenantAdmin && u.IsActive) && !await OtherActiveAdminAsync(u.Id, ct))
            return Problem("Keep at least one active admin. Make someone else an admin first.", statusCode: 409);
        await _db.SaveChangesAsync(ct);
        _status.Forget(u.Id); AccessResolver.Invalidate(TenantId);
        await _audit.LogAsync("user.update", "User", u.Id.ToString(), u.Email, ct: ct);
        return Ok(await ToMember(u, ct));
    }

    /// <summary>A new single-use link to choose a password (an invitation again if they never chose one). E-mailed when a mail server is set up, otherwise returned to copy.</summary>
    [HttpPost("{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id, CancellationToken ct)
    {
        var u = await Mine.FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct); if (u == null) return NotFound();
        var invite = u.PasswordHash == null;
        var token = await _accounts.NewTokenAsync(u, invite ? AccountService.Invite : AccountService.Reset, invite ? TimeSpan.FromDays(_accounts.Options.InviteDays) : TimeSpan.FromMinutes(_accounts.Options.ResetMinutes), ct);
        var link = _mailer.Link(AccountSupport.BaseUrl(Request), invite ? "invite" : "reset", token);
        var company = await _db.Tenants.Where(t => t.Id == TenantId).Select(t => t.Name).FirstAsync(ct);
        var sent = _mailer.Enabled && (invite ? await _mailer.SendInviteAsync(u, company, link, ct) : await _mailer.SendResetAsync(u, link, ct));
        await _audit.LogAsync("user.reset-link", "User", u.Id.ToString(), u.Email, ct: ct);
        return Ok(new InviteResult(await ToMember(u, ct), sent, sent ? null : link));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var u = await Mine.FirstOrDefaultAsync(x => x.Id == id, ct); if (u == null) return NotFound();
        if (AccountSupport.AccountId(User) == u.Id) return Problem("You cannot delete your own account.", statusCode: 409);
        if (u.Role == Roles.TenantAdmin && u.IsActive && !await OtherActiveAdminAsync(u.Id, ct)) return Problem("Keep at least one active admin.", statusCode: 409);
        _db.UserAccounts.Remove(u);
        await _db.SaveChangesAsync(ct);
        _status.Forget(u.Id); AccessResolver.Invalidate(TenantId);
        await _audit.LogAsync("user.delete", "User", u.Id.ToString(), u.Email, ct: ct);
        return NoContent();
    }

    /// <summary>Another active admin account exists, or the caller is the company's own key (which is an admin that cannot be switched off from here).</summary>
    private async Task<bool> OtherActiveAdminAsync(Guid exceptId, CancellationToken ct) =>
        AccountSupport.AccountId(User) == null || await Mine.AnyAsync(x => x.Id != exceptId && x.Role == Roles.TenantAdmin && x.IsActive, ct);
}

/// <summary>Platform administrators with an e-mail account (the platform key keeps working as the emergency way in).</summary>
[ApiController]
[Route("api/v1/platform-admins")]
[Authorize(Policy = "PlatformAdmin")]
public class PlatformAdminsController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly AccountService _accounts;
    private readonly AccountMailer _mailer;
    private readonly TokenStatusChecker _status;
    private readonly IAuditLogger _audit;

    public PlatformAdminsController(FlowMindDbContext db, AccountService accounts, AccountMailer mailer, TokenStatusChecker status, IAuditLogger audit)
    { _db = db; _accounts = accounts; _mailer = mailer; _status = status; _audit = audit; }

    private static TeamMember ToMember(UserAccount u) => new(u.Id, u.Email, u.DisplayName, u.Role, null, null, u.IsActive, u.PasswordHash == null, u.LockedUntilUtc > DateTime.UtcNow, u.CreatedUtc, u.LastLoginUtc);

    [HttpGet]
    public async Task<IReadOnlyList<TeamMember>> List(CancellationToken ct) =>
        (await _db.UserAccounts.Where(u => u.TenantId == null).OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(ToMember).ToList();

    [HttpPost]
    public async Task<IActionResult> Invite(InviteRequest req, CancellationToken ct)
    {
        try
        {
            var (u, token) = await _accounts.InviteAsync(null, req.Email, req.Name, Roles.PlatformAdmin, null, ct);
            var link = _mailer.Link(AccountSupport.BaseUrl(Request), "invite", token);
            var sent = _mailer.Enabled && await _mailer.SendInviteAsync(u, "", link, ct);
            await _audit.LogAsync("platform.admin-invite", "User", u.Id.ToString(), u.Email, ct: ct);
            return Ok(new InviteResult(ToMember(u), sent, sent ? null : link));
        }
        catch (AccountException ex) { return Problem(ex.Message, statusCode: ex.Status); }
    }

    [HttpPost("{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id, CancellationToken ct)
    {
        var u = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == null && x.IsActive, ct); if (u == null) return NotFound();
        var invite = u.PasswordHash == null;
        var token = await _accounts.NewTokenAsync(u, invite ? AccountService.Invite : AccountService.Reset, invite ? TimeSpan.FromDays(_accounts.Options.InviteDays) : TimeSpan.FromMinutes(_accounts.Options.ResetMinutes), ct);
        var link = _mailer.Link(AccountSupport.BaseUrl(Request), invite ? "invite" : "reset", token);
        var sent = _mailer.Enabled && (invite ? await _mailer.SendInviteAsync(u, "", link, ct) : await _mailer.SendResetAsync(u, link, ct));
        return Ok(new InviteResult(ToMember(u), sent, sent ? null : link));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var u = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == null, ct); if (u == null) return NotFound();
        if (AccountSupport.AccountId(User) == u.Id) return Problem("You cannot delete your own account.", statusCode: 409);
        _db.UserAccounts.Remove(u);
        await _db.SaveChangesAsync(ct);
        _status.Forget(u.Id);
        await _audit.LogAsync("platform.admin-delete", "User", u.Id.ToString(), u.Email, ct: ct);
        return NoContent();
    }
}
