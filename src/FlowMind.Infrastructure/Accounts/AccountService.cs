using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Accounts;

public sealed class AccountOptions
{
    public const string Section = "Accounts";
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int ResetMinutes { get; set; } = 30;
    public int InviteDays { get; set; } = 7;
    /// <summary>The address people use to reach FlowMind, for example https://flowmind.hospital.local. Links in e-mails are built from it. Empty = the address of the request.</summary>
    public string PublicUrl { get; set; } = "";
}

public sealed class AccountException : Exception
{
    public int Status { get; }
    public AccountException(int status, string message) : base(message) { Status = status; }
}

public enum LoginOutcome { Ok, Invalid, Locked, Disabled }
public sealed record LoginResult(LoginOutcome Outcome, UserAccount? User);
public sealed record TokenInfo(string Email, string DisplayName, string Purpose);

/// <summary>Everything about signing in with an e-mail and password: checking it, lock-out after repeated failures, invitations, reset links and changing the password.</summary>
public sealed class AccountService
{
    public const string Reset = "reset", Invite = "invite";
    private readonly FlowMindDbContext _db;
    private readonly AccountOptions _o;
    public AccountService(FlowMindDbContext db, IOptions<AccountOptions> o) { _db = db; _o = o.Value; }

    public AccountOptions Options => _o;

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool IsValidEmail(string? email)
    {
        var e = NormalizeEmail(email);
        if (e.Length is < 5 or > 254 || e.Contains(' ')) return false;
        try { var a = new MailAddress(e); return a.Address == e && a.Host.Contains('.'); } catch (FormatException) { return false; }
    }

    public Task<bool> EmailExistsAsync(string? email, CancellationToken ct) { var e = NormalizeEmail(email); return _db.UserAccounts.AnyAsync(x => x.Email == e, ct); }

    // ---- sign-in -----------------------------------------------------------------------------------------------------------

    public async Task<LoginResult> LoginAsync(string? email, string? password, CancellationToken ct)
    {
        var e = NormalizeEmail(email); var pw = password ?? "";
        var u = e.Length == 0 ? null : await _db.UserAccounts.FirstOrDefaultAsync(x => x.Email == e, ct);
        if (u == null) { PasswordHasher.BurnTime(pw); return new(LoginOutcome.Invalid, null); }
        if (u.LockedUntilUtc is { } until && until > DateTime.UtcNow) { PasswordHasher.BurnTime(pw); return new(LoginOutcome.Locked, null); }

        var ok = PasswordHasher.Verify(pw, u.PasswordHash, out var rehash);
        if (!ok)
        {
            if (u.PasswordHash == null) PasswordHasher.BurnTime(pw);
            u.FailedLogins++;
            if (u.FailedLogins >= _o.MaxFailedLogins) { u.LockedUntilUtc = DateTime.UtcNow.AddMinutes(_o.LockoutMinutes); u.FailedLogins = 0; }
            await _db.SaveChangesAsync(ct);
            return new(LoginOutcome.Invalid, null);
        }
        // The password is right; only now say that the account (or its company) is switched off.
        if (!u.IsActive || (u.TenantId is { } t && !await _db.Tenants.AnyAsync(x => x.Id == t && x.IsActive, ct))) return new(LoginOutcome.Disabled, null);

        u.FailedLogins = 0; u.LockedUntilUtc = null; u.LastLoginUtc = DateTime.UtcNow;
        if (rehash) u.PasswordHash = PasswordHasher.Hash(pw);
        await _db.SaveChangesAsync(ct);
        return new(LoginOutcome.Ok, u);
    }

    // ---- invitations and reset links -------------------------------------------------------------------------------------------

    /// <summary>Creates the account without a password and a one-time invitation to choose one. The e-mail must not be used by anyone yet.</summary>
    public async Task<(UserAccount User, string Token)> InviteAsync(Guid? tenantId, string email, string displayName, string role, Guid? audienceId, CancellationToken ct)
    {
        var e = NormalizeEmail(email);
        if (!IsValidEmail(e)) throw new AccountException(400, "Enter a valid e-mail address.");
        var name = (displayName ?? "").Trim();
        if (name.Length is < 2 or > 100) throw new AccountException(400, "Enter the person's name (2 to 100 characters).");
        if (await _db.UserAccounts.AnyAsync(x => x.Email == e, ct)) throw new AccountException(409, "Someone already has an account with that e-mail address.");
        var u = new UserAccount { TenantId = tenantId, Email = e, DisplayName = name, Role = role, AudienceId = role == "TenantUser" ? audienceId : null };
        _db.UserAccounts.Add(u);
        await _db.SaveChangesAsync(ct);
        return (u, await NewTokenAsync(u, Invite, TimeSpan.FromDays(_o.InviteDays), ct));
    }

    /// <summary>A new single-use link. Any earlier unused link of this person stops working.</summary>
    public async Task<string> NewTokenAsync(UserAccount u, string purpose, TimeSpan life, CancellationToken ct)
    {
        foreach (var old in await _db.AccountTokens.Where(t => t.UserId == u.Id && t.UsedUtc == null).ToListAsync(ct)) old.UsedUtc = DateTime.UtcNow;
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _db.AccountTokens.Add(new AccountToken { UserId = u.Id, Purpose = purpose, TokenHash = Hash(raw), ExpiresUtc = DateTime.UtcNow.Add(life) });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<UserAccount?> FindActiveByEmailAsync(string? email, CancellationToken ct)
    {
        var e = NormalizeEmail(email);
        if (e.Length == 0) return null;
        var u = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Email == e && x.IsActive, ct);
        if (u?.TenantId is { } t && !await _db.Tenants.AnyAsync(x => x.Id == t && x.IsActive, ct)) return null;
        return u;
    }

    /// <summary>What a link is for, or null when it is unknown, used or expired. Lets the page show whose password is being set.</summary>
    public async Task<TokenInfo?> PeekAsync(string? rawToken, CancellationToken ct)
    {
        var (t, u) = await FindAsync(rawToken, ct);
        return t == null || u == null ? null : new TokenInfo(u.Email, u.DisplayName, t.Purpose);
    }

    /// <summary>Uses a link to set a password. The link is spent, every other unused link of the person is cancelled and every old session ends.</summary>
    public async Task<UserAccount> RedeemAsync(string? rawToken, string? newPassword, CancellationToken ct)
    {
        var (t, u) = await FindAsync(rawToken, ct);
        if (t == null || u == null) throw new AccountException(400, "This link has expired or was already used. Ask for a new one.");
        if (PasswordPolicy.Check(newPassword, u.Email, u.DisplayName) is { } why) throw new AccountException(400, why);
        SetPassword(u, newPassword!);
        foreach (var other in await _db.AccountTokens.Where(x => x.UserId == u.Id && x.UsedUtc == null).ToListAsync(ct)) other.UsedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return u;
    }

    private async Task<(AccountToken?, UserAccount?)> FindAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 200) return (null, null);
        var h = Hash(rawToken.Trim());
        var t = await _db.AccountTokens.FirstOrDefaultAsync(x => x.TokenHash == h && x.UsedUtc == null && x.ExpiresUtc > DateTime.UtcNow, ct);
        if (t == null) return (null, null);
        var u = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == t.UserId && x.IsActive, ct);
        if (u?.TenantId is { } tid && !await _db.Tenants.AnyAsync(x => x.Id == tid && x.IsActive, ct)) return (null, null);
        return (t, u);
    }

    // ---- signed-in person ------------------------------------------------------------------------------------------------------

    public async Task ChangePasswordAsync(UserAccount u, string? current, string? next, CancellationToken ct)
    {
        if (!PasswordHasher.Verify(current ?? "", u.PasswordHash, out _)) throw new AccountException(400, "The current password is not right.");
        if (PasswordPolicy.Check(next, u.Email, u.DisplayName) is { } why) throw new AccountException(400, why);
        if (PasswordHasher.Verify(next!, u.PasswordHash, out _)) throw new AccountException(400, "Choose a password you have not used just now.");
        SetPassword(u, next!);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Ends every session of this person (they sign in again). Used by "sign out everywhere" and when an admin turns the account off.</summary>
    public async Task EndSessionsAsync(UserAccount u, CancellationToken ct) { u.SecurityStamp = Guid.NewGuid(); await _db.SaveChangesAsync(ct); }

    private static void SetPassword(UserAccount u, string password)
    {
        u.PasswordHash = PasswordHasher.Hash(password);
        u.PasswordChangedUtc = DateTime.UtcNow;
        u.SecurityStamp = Guid.NewGuid();
        u.FailedLogins = 0; u.LockedUntilUtc = null;
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}

/// <summary>The e-mails FlowMind sends. Plain text, short, and they only ever contain a link that works once.</summary>
public sealed class AccountMailer
{
    private readonly IEmailSender _mail;
    private readonly AccountOptions _o;
    public AccountMailer(IEmailSender mail, IOptions<AccountOptions> o) { _mail = mail; _o = o.Value; }

    public bool Enabled => _mail.Enabled;

    /// <summary>The page address for a token, built from the configured public address (or the request address when none is set).</summary>
    public string Link(string fallbackBase, string page, string token) => $"{(string.IsNullOrWhiteSpace(_o.PublicUrl) ? fallbackBase : _o.PublicUrl).TrimEnd('/')}/#/{page}?token={Uri.EscapeDataString(token)}";

    public Task<bool> SendInviteAsync(UserAccount u, string companyName, string link, CancellationToken ct) => _mail.SendAsync(u.Email, "You have been invited to BBNavigator",
        $"Hello {u.DisplayName},\n\nYou have been invited to BBNavigator{(string.IsNullOrWhiteSpace(companyName) ? "" : " for " + companyName)}.\n\nChoose your password here (the link works once and expires in {_o.InviteDays} days):\n{link}\n\nYour sign-in is this e-mail address.\n\nIf you were not expecting this, ignore this message.\n", ct);

    public Task<bool> SendResetAsync(UserAccount u, string link, CancellationToken ct) => _mail.SendAsync(u.Email, "Reset your BBNavigator password",
        $"Hello {u.DisplayName},\n\nSomeone asked to reset the password of this BBNavigator account. To choose a new one, open this link (it works once and expires in {_o.ResetMinutes} minutes):\n{link}\n\nIf it was not you, ignore this message. Your password stays as it is.\n", ct);
}
