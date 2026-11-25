namespace FlowMind.Domain.Entities;

/// <summary>
/// A person who signs in with an e-mail address and a password. The e-mail identifies the person AND the company (one e-mail = one account),
/// so there is no company ID to remember. Not tenant-filtered on purpose: sign-in has to find the account before the company is known, and
/// platform administrators belong to no company. Every query made on behalf of a company must filter on <see cref="TenantId"/> explicitly.
/// </summary>
public class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The company this person belongs to. Null = platform administrator.</summary>
    public Guid? TenantId { get; set; }
    /// <summary>Stored in lower case; unique across the whole server.</summary>
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Null until the person has chosen a password from their invitation. "v1$iterations$salt$hash".</summary>
    public string? PasswordHash { get; set; }
    /// <summary>"PlatformAdmin", "TenantAdmin" or "TenantUser".</summary>
    public string Role { get; set; } = "TenantUser";
    /// <summary>For ordinary users: which parts of the documents they may see. Null = everything approved.</summary>
    public Guid? AudienceId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
    public DateTime? PasswordChangedUtc { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    /// <summary>Changes whenever the password changes or the person is signed out everywhere. A session carries the value it was issued with, so old sessions stop working.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
}

/// <summary>A single-use link: "invite" (choose a first password) or "reset" (choose a new one). Only the hash of the secret is stored.</summary>
public class AccountToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = "reset";
    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}
