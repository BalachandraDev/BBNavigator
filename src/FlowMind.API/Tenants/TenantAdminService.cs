using FlowMind.Infrastructure.Security;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Tenants;

public sealed record TenantRow(Guid Id, string Name, string Slug, bool IsActive, DateTime CreatedUtc, int Applications, int Documents, int UserKeys, bool KeyViewable);
public sealed record TenantLogin(string CompanyId, string Key);

/// <summary>An expected failure with the HTTP status the API should answer with.</summary>
public sealed class TenantAdminException : Exception
{
    public int Status { get; }
    public TenantAdminException(int status, string message) : base(message) => Status = status;
}

/// <summary>
/// Everything the platform administrator can do to a company. Each action is audited. Deleting a company removes ALL of its
/// data (database rows, search vectors, stored files); usage counters and the audit trail are kept on purpose.
/// </summary>
public sealed class TenantAdminService
{
    private readonly FlowMindDbContext _db;
    private readonly SecretBox _box;
    private readonly IVectorStore _vectors;
    private readonly IFileStorage _files;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TenantAdminService> _log;

    public TenantAdminService(FlowMindDbContext db, SecretBox box, IVectorStore vectors, IFileStorage files, IAuditLogger audit, ILogger<TenantAdminService> log)
    {
        _db = db; _box = box; _vectors = vectors; _files = files; _audit = audit; _log = log;
    }

    public async Task<IReadOnlyList<TenantRow>> ListAsync(CancellationToken ct) =>
        await _db.Tenants.OrderBy(t => t.CreatedUtc).Select(t => new TenantRow(
            t.Id, t.Name, t.Slug, t.IsActive, t.CreatedUtc,
            _db.Applications.Count(a => a.TenantId == t.Id),
            _db.Documents.Count(d => d.TenantId == t.Id),
            _db.AccessKeys.Count(k => k.TenantId == t.Id && k.IsActive),
            t.ApiKeyCipher != null)).ToListAsync(ct);

    public async Task<(Tenant Tenant, string Key)> CreateAsync(string name, string slug, CancellationToken ct)
    {
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct)) throw new TenantAdminException(409, "A company with this Company ID already exists.");
        var key = ApiKeys.Generate();
        var tenant = new Tenant { Name = name.Trim(), Slug = slug };
        Apply(tenant, key);
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tenant.create", "Tenant", tenant.Id.ToString(), tenant.Slug, tenant.Id, ct);
        return (tenant, key);
    }

    /// <summary>Show the company's sign-in (Company ID + key). Audited. Fails clearly when the key was never stored.</summary>
    public async Task<TenantLogin> RevealAsync(Guid id, CancellationToken ct)
    {
        var t = await Find(id, ct);
        var key = _box.TryDecrypt(t.ApiKeyCipher)
            ?? throw new TenantAdminException(409, "This company's key is not stored (it was created before key storage existed, or the server secret changed). Use Reset key to issue a new one.");
        await _audit.LogAsync("tenant.key.reveal", "Tenant", t.Id.ToString(), t.Slug, t.Id, ct);
        return new TenantLogin(t.Slug, key);
    }

    /// <summary>Issue a new key. The old key stops working immediately and company-admin sessions opened with it end.</summary>
    public async Task<TenantLogin> ResetKeyAsync(Guid id, CancellationToken ct)
    {
        var t = await Find(id, ct);
        var key = ApiKeys.Generate();
        Apply(t, key);
        t.TokensValidAfterUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tenant.key.reset", "Tenant", t.Id.ToString(), t.Slug, t.Id, ct);
        return new TenantLogin(t.Slug, key);
    }

    /// <summary>Disable or re-enable a company's access. Sign-in stops at once and open sessions end within seconds.</summary>
    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var t = await Find(id, ct);
        if (t.IsActive == active) return;
        t.IsActive = active;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(active ? "tenant.enable" : "tenant.disable", "Tenant", t.Id.ToString(), t.Slug, t.Id, ct);
    }

    /// <summary>
    /// Permanently delete a company and everything it owns. The caller must type the Company ID exactly. Order matters:
    /// lock the company out first, remove the search vectors (abort if that fails, so nothing is half-deleted), then the
    /// database rows in one transaction, then the stored files.
    /// </summary>
    public async Task DeleteAsync(Guid id, string? confirmCompanyId, CancellationToken ct)
    {
        var t = await Find(id, ct);
        if (!string.Equals(confirmCompanyId, t.Slug, StringComparison.Ordinal))
            throw new TenantAdminException(400, "To delete a company, confirm its Company ID exactly.");

        t.IsActive = false; t.TokensValidAfterUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _vectors.DeleteTenantAsync(id, ct);
        await PurgeRowsAsync(id, ct);

        try { await _files.DeleteTenantAsync(id, ct); }
        catch (Exception ex) { _log.LogError(ex, "Company {Company} was deleted but some stored files could not be removed", t.Slug); }

        await _audit.LogAsync("tenant.delete", "Tenant", id.ToString(), t.Slug, id, ct);
    }

    private async Task PurgeRowsAsync(Guid id, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

            // Children before parents (foreign keys). Usage counters and the audit trail are deliberately kept.
            await Del<ConversationMessage>(id, ct); await Del<Conversation>(id, ct); await Del<ChatCacheEntry>(id, ct); await Del<AnalysisCacheEntry>(id, ct);
            await Del<SupportRequest>(id, ct); await Del<QuestionGapStatus>(id, ct); await Del<StorageSetting>(id, ct);
            await Del<AccessRuleAudience>(id, ct); await Del<AccessRule>(id, ct);
            await Del<WorkflowTransition>(id, ct); await Del<WorkflowStep>(id, ct); await Del<Workflow>(id, ct); await Del<PageLink>(id, ct);
            await Del<RolePermission>(id, ct); await Del<BusinessRule>(id, ct); await Del<ValidationRule>(id, ct); await Del<PageFunctionality>(id, ct);
            await Del<AppPage>(id, ct); await Del<Module>(id, ct);
            await Del<DocumentProcessingJob>(id, ct); await Del<DocumentPage>(id, ct); await Del<DocumentVersion>(id, ct); await Del<Document>(id, ct);
            await Del<BotLink>(id, ct); await Del<TenantApplication>(id, ct); await Del<TenantAccessKey>(id, ct); await Del<Audience>(id, ct);

            var people = _db.UserAccounts.Where(u => u.TenantId == id);
            if (_db.Database.IsRelational()) { await _db.AccountTokens.Where(t => people.Any(u => u.Id == t.UserId)).ExecuteDeleteAsync(ct); await people.ExecuteDeleteAsync(ct); }
            else { var ids = await people.Select(u => u.Id).ToListAsync(ct); _db.AccountTokens.RemoveRange(await _db.AccountTokens.Where(t => ids.Contains(t.UserId)).ToListAsync(ct)); _db.UserAccounts.RemoveRange(await people.ToListAsync(ct)); await _db.SaveChangesAsync(ct); }

            var tenants = _db.Tenants.Where(x => x.Id == id);
            if (_db.Database.IsRelational()) await tenants.ExecuteDeleteAsync(ct);
            else { _db.Tenants.RemoveRange(await tenants.ToListAsync(ct)); await _db.SaveChangesAsync(ct); }

            if (tx != null) await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
    }

    private async Task Del<T>(Guid tenantId, CancellationToken ct) where T : class
    {
        // IgnoreQueryFilters: soft-deleted documents must go too.
        var rows = _db.Set<T>().IgnoreQueryFilters().Where(x => EF.Property<Guid>(x, "TenantId") == tenantId);
        if (_db.Database.IsRelational()) await rows.ExecuteDeleteAsync(ct);
        else { _db.Set<T>().RemoveRange(await rows.ToListAsync(ct)); await _db.SaveChangesAsync(ct); }
    }

    private void Apply(Tenant t, string key)
    {
        t.ApiKeyPrefix = key[..8];
        t.ApiKeyHash = ApiKeys.Hash(key);                       // sign-in always verifies the hash
        t.ApiKeyCipher = _box.Enabled ? _box.Encrypt(key) : null; // recoverable copy only when a server secret exists
    }

    private async Task<Tenant> Find(Guid id, CancellationToken ct) =>
        await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new TenantAdminException(404, "Company not found.");
}
