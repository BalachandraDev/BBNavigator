using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FlowMind.API.Authentication;

/// <summary>
/// A JWT is a signed statement that stays valid until it expires. This closes the gap for the two things an admin can
/// switch off: a deactivated company and a revoked user access key. Results are cached briefly, so a revocation takes
/// effect within <see cref="CacheFor"/> on every server instance without a database hit per request.
/// </summary>
public sealed class TokenStatusChecker
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly IMemoryCache _cache;
    public TokenStatusChecker(IMemoryCache cache) => _cache = cache;

    public async Task<bool> IsStillValidAsync(ClaimsPrincipal principal, FlowMindDbContext db, CancellationToken ct)
    {
        var role = principal.FindFirstValue(ClaimNames.Role);
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
        if (subject.StartsWith("user:")) return await AccountStillValid(principal, subject, role, db, ct);   // a person with an e-mail account
        if (role == Roles.PlatformAdmin) return true;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimNames.Tenant), out var tenantId)) return true; // policies reject it later

        var state = await CachedState($"tenant-state:{tenantId:N}", async () =>
            await db.Tenants.Where(t => t.Id == tenantId).Select(t => new TenantState(t.IsActive, t.TokensValidAfterUtc)).FirstOrDefaultAsync(ct) ?? new TenantState(false, null));
        if (!state.Active) return false;

        // A key reset ends company-admin sessions opened before it. Token times have 1-second resolution, so compare at that resolution.
        if (role == Roles.TenantAdmin && state.ValidAfter is { } after)
        {
            var issued = long.TryParse(principal.FindFirstValue("iat"), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime : DateTime.MinValue;
            var cutoff = new DateTime(after.Ticks - after.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            if (issued < cutoff) return false;
        }

        if (role == Roles.TenantUser)
        {
            var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            if (!sub.StartsWith("key:") || !Guid.TryParseExact(sub[4..], "N", out var keyId)) return false;
            return await Cached($"key:{keyId:N}", () => db.AccessKeys.IgnoreQueryFilters()
                .AnyAsync(k => k.Id == keyId && k.TenantId == tenantId && k.IsActive, ct));
        }
        return true;
    }

    private sealed record AccountState(bool Active, string Stamp, string Role, Guid? TenantId);

    /// <summary>The account must still exist and be switched on, hold the same role, and carry the current security stamp (a password change or "sign out everywhere" changes it).</summary>
    private async Task<bool> AccountStillValid(ClaimsPrincipal principal, string subject, string? role, FlowMindDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParseExact(subject[5..], "N", out var userId)) return false;
        var key = $"account-state:{userId:N}";
        if (!_cache.TryGetValue(key, out AccountState? st) || st == null)
        {
            st = await db.UserAccounts.Where(u => u.Id == userId).Select(u => new AccountState(u.IsActive, u.SecurityStamp.ToString("N"), u.Role, u.TenantId)).FirstOrDefaultAsync(ct);
            if (st == null) return false;
            _cache.Set(key, st, CacheFor);
        }
        if (!st.Active || st.Role != role || st.Stamp != principal.FindFirstValue(ClaimNames.Stamp)) return false;
        if (st.TenantId is { } t)
        {
            if (principal.FindFirstValue(ClaimNames.Tenant) != t.ToString()) return false;
            var tenant = await CachedState($"tenant-state:{t:N}", async () => await db.Tenants.Where(x => x.Id == t).Select(x => new TenantState(x.IsActive, x.TokensValidAfterUtc)).FirstOrDefaultAsync(ct) ?? new TenantState(false, null));
            return tenant.Active;
        }
        return role == Roles.PlatformAdmin;
    }

    /// <summary>Drops the cached state of one account on this server, so a change takes effect at once instead of within <see cref="CacheFor"/>.</summary>
    public void Forget(Guid userId) => _cache.Remove($"account-state:{userId:N}");

    private sealed record TenantState(bool Active, DateTime? ValidAfter);

    private async Task<TenantState> CachedState(string key, Func<Task<TenantState>> load)
    {
        if (_cache.TryGetValue(key, out TenantState? hit) && hit != null) return hit;
        var value = await load();
        _cache.Set(key, value, CacheFor);
        return value;
    }

    private async Task<bool> Cached(string key, Func<Task<bool>> load)
    {
        if (_cache.TryGetValue(key, out bool hit)) return hit;
        var value = await load();
        _cache.Set(key, value, CacheFor);
        return value;
    }
}
