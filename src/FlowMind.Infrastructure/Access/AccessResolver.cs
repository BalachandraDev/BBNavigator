using System.Collections.Concurrent;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FlowMind.Infrastructure.Access;

/// <summary>
/// What one caller may see. <see cref="Full"/> = everything approved (company admins, and keys or links with no audience).
/// Anything else lists exactly the modules, pages and workflows the audience's rules open, and the document pages that follow from them.
/// </summary>
public sealed class AccessScope
{
    public static readonly AccessScope Full = new() { Unrestricted = true, Name = "Full access", CanBrowseKnowledge = true };
    /// <summary>Fails closed: used when a caller cannot be matched to a key or audience.</summary>
    public static readonly AccessScope None = new() { Name = "No access", Pages = new PageAccess(Array.Empty<Guid>(), Array.Empty<(Guid, int)>()) };

    public bool Unrestricted { get; init; }
    /// <summary>Whether a user key with this scope gets the Knowledge page. Full access always does; an audience only when its admin switched it on.</summary>
    public bool CanBrowseKnowledge { get; init; }
    public string Name { get; init; } = "";
    /// <summary>The document pages this caller may be answered from. Null only when unrestricted.</summary>
    public PageAccess? Pages { get; init; }
    public IReadOnlySet<Guid> Modules { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> AppPages { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> Workflows { get; init; } = new HashSet<Guid>();

    public bool AllowsModule(Guid id) => Unrestricted || Modules.Contains(id);
    public bool AllowsPage(Guid id) => Unrestricted || AppPages.Contains(id);
    public bool AllowsWorkflow(Guid id) => Unrestricted || Workflows.Contains(id);
}

/// <summary>
/// Turns "this key / this link / this audience" into an <see cref="AccessScope"/>. Results are cached for a few seconds (like key revocation),
/// and dropped at once on this server whenever rules, audiences or key assignments change.
/// </summary>
public sealed class AccessResolver
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<Guid, int> Versions = new();

    private readonly FlowMindDbContext _db;
    private readonly IMemoryCache _cache;
    public AccessResolver(FlowMindDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    /// <summary>Call after any change to rules, audiences or which audience a key or link has.</summary>
    public static void Invalidate(Guid tenantId) => Versions.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
    private static int Version(Guid tenantId) => Versions.GetValueOrDefault(tenantId);

    /// <summary>The scope of a user access key. An unknown or revoked key gets no access at all.</summary>
    public async Task<AccessScope> ForKeyAsync(Guid tenantId, Guid keyId, CancellationToken ct)
    {
        var cacheKey = $"access-key:{tenantId:N}:{Version(tenantId)}:{keyId:N}";
        if (!_cache.TryGetValue(cacheKey, out Guid? audience))   // a cached null means "this key has no audience" = full access
        {
            var key = await _db.AccessKeys.IgnoreQueryFilters().Where(k => k.Id == keyId && k.TenantId == tenantId && k.IsActive)
                .Select(k => new { k.AudienceId }).FirstOrDefaultAsync(ct);
            if (key == null) return AccessScope.None;
            audience = key.AudienceId;
            _cache.Set(cacheKey, audience, CacheFor);
        }
        return await ForAudienceAsync(tenantId, audience, ct);
    }

    /// <summary>The scope of a signed-in person (e-mail account). A switched-off or unknown account gets no access; an account with no audience sees everything approved.</summary>
    public async Task<AccessScope> ForUserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var cacheKey = $"access-user:{tenantId:N}:{Version(tenantId)}:{userId:N}";
        if (!_cache.TryGetValue(cacheKey, out Guid? audience))
        {
            var user = await _db.UserAccounts.Where(u => u.Id == userId && u.TenantId == tenantId && u.IsActive).Select(u => new { u.AudienceId }).FirstOrDefaultAsync(ct);
            if (user == null) return AccessScope.None;
            audience = user.AudienceId;
            _cache.Set(cacheKey, audience, CacheFor);
        }
        return await ForAudienceAsync(tenantId, audience, ct);
    }

    /// <summary>The scope of an audience. Null audience = full access.</summary>
    public async Task<AccessScope> ForAudienceAsync(Guid tenantId, Guid? audienceId, CancellationToken ct)
    {
        if (audienceId == null) return AccessScope.Full;
        var cacheKey = $"access-scope:{tenantId:N}:{Version(tenantId)}:{audienceId.Value:N}";
        if (_cache.TryGetValue(cacheKey, out AccessScope? hit) && hit != null) return hit;

        var audience = await _db.Audiences.IgnoreQueryFilters().Where(a => a.Id == audienceId && a.TenantId == tenantId).Select(a => new { a.Name, a.CanBrowseKnowledge }).FirstOrDefaultAsync(ct);
        if (audience == null) return AccessScope.None;   // a dangling audience must never turn into full access

        var model = await AccessModel.LoadAsync(_db, tenantId, ct);
        var scope = Build(model, audienceId.Value, audience.Name, audience.CanBrowseKnowledge);
        _cache.Set(cacheKey, scope, CacheFor);
        return scope;
    }

    /// <summary>Works out an audience's scope from the loaded model. Public so the admin "preview" and the tests use exactly the same logic.</summary>
    public static AccessScope Build(AccessModel model, Guid audienceId, string name, bool canBrowseKnowledge = false)
    {
        var full = new List<Guid>();
        var partial = new List<(Guid, int)>();
        foreach (var d in model.Documents)
        {
            var open = d.Pages.Where(p => model.IsPageOpen(d.Id, p, audienceId)).ToList();
            if (open.Count == 0) continue;
            if (open.Count == d.Pages.Count) full.Add(d.Id);
            else partial.AddRange(open.Select(p => (d.Id, p)));
        }
        return new AccessScope
        {
            Name = name,
            CanBrowseKnowledge = canBrowseKnowledge,
            Pages = new PageAccess(full, partial),
            Modules = model.Modules.Where(m => model.IsOpen(new Owner("module", m.Id), audienceId)).Select(m => m.Id).ToHashSet(),
            AppPages = model.Pages.Where(p => model.IsOpen(new Owner("page", p.Id), audienceId)).Select(p => p.Id).ToHashSet(),
            Workflows = model.Workflows.Where(w => model.IsOpen(new Owner("workflow", w.Id), audienceId)).Select(w => w.Id).ToHashSet(),
        };
    }
}
