using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FlowMind.Infrastructure.Access;

namespace FlowMind.API.Authentication;

public static class AccessScopeExtensions
{
    /// <summary>
    /// What the signed-in caller may see. Company admins see everything. A user key sees what its audience's rules open, or everything when the key
    /// has no audience. A key that cannot be identified sees nothing. The key comes from the validated token or API-key header, never from the request.
    /// </summary>
    public static async Task<AccessScope> ResolveScopeAsync(this ClaimsPrincipal user, AccessResolver resolver, Guid tenantId, CancellationToken ct)
    {
        if (!user.IsInRole(Roles.TenantUser)) return AccessScope.Full;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
        if (sub.StartsWith("user:") && Guid.TryParseExact(sub[5..], "N", out var userId)) return await resolver.ForUserAsync(tenantId, userId, ct);
        if (!sub.StartsWith("key:") || !Guid.TryParseExact(sub[4..], "N", out var keyId)) return AccessScope.None;
        return await resolver.ForKeyAsync(tenantId, keyId, ct);
    }
}
