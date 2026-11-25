using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Database;

public class FlowMindDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public FlowMindDbContext(DbContextOptions<FlowMindDbContext> options, ITenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    /// <summary>
    /// Tenant of the current request. Null means "unscoped" (platform admin or the background worker);
    /// tenant-facing endpoints always require a tenant claim, so they are always scoped.
    /// </summary>
    public Guid? CurrentTenantId => _tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantApplication> Applications => Set<TenantApplication>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<DocumentPage> DocumentPages => Set<DocumentPage>();
    public DbSet<DocumentProcessingJob> ProcessingJobs => Set<DocumentProcessingJob>();
    public DbSet<AIUsageLog> AiUsageLogs => Set<AIUsageLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Structured application knowledge (Phase 3)
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<AppPage> AppPages => Set<AppPage>();
    public DbSet<PageFunctionality> PageFunctionalities => Set<PageFunctionality>();
    public DbSet<ValidationRule> ValidationRules => Set<ValidationRule>();
    public DbSet<BusinessRule> BusinessRules => Set<BusinessRule>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PageLink> PageLinks => Set<PageLink>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<AnalysisCacheEntry> AnalysisCache => Set<AnalysisCacheEntry>();

    // Chat (Phase 5)
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<TenantAccessKey> AccessKeys => Set<TenantAccessKey>();
    public DbSet<ChatCacheEntry> ChatCache => Set<ChatCacheEntry>();
    public DbSet<BotLink> BotLinks => Set<BotLink>();

    // Insights (Phase 10)
    public DbSet<QuestionGapStatus> QuestionGapStatuses => Set<QuestionGapStatus>();
    public DbSet<SupportRequest> SupportRequests => Set<SupportRequest>();
    public DbSet<StorageSetting> StorageSettings => Set<StorageSetting>();
    public DbSet<AllowedFolder> AllowedFolders => Set<AllowedFolder>();

    // Accounts: e-mail and password sign-in (Phase 18)
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();

    // Audiences and access rules (Phase 12)
    public DbSet<Audience> Audiences => Set<Audience>();
    public DbSet<AccessRule> AccessRules => Set<AccessRule>();
    public DbSet<AccessRuleAudience> AccessRuleAudiences => Set<AccessRuleAudience>();

    private void ConfigureKnowledge<T>(ModelBuilder b, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>>? extra = null)
        where T : KnowledgeItem
    {
        b.Entity<T>(e =>
        {
            // Tenant isolation filter + index for the common "everything in this application" query.
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.TenantId, x.ApplicationId });
            e.HasIndex(x => x.SourceDocumentVersionId);
            extra?.Invoke(e);
        });
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApiKeyPrefix).HasMaxLength(16);
            e.Property(x => x.ApiKeyHash).HasMaxLength(64);
            e.Property(x => x.ApiKeyCipher).HasMaxLength(1000);
        });

        // Database-level isolation safeguard: every tenant-owned entity is filtered by the current tenant.
        b.Entity<TenantApplication>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<Document>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TenantApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.ApplicationId });
            // Soft delete + tenant filter.
            e.HasQueryFilter(x => x.DeletedUtc == null && (CurrentTenantId == null || x.TenantId == CurrentTenantId));
        });

        b.Entity<DocumentVersion>(e =>
        {
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.StorageKind).HasMaxLength(10).IsRequired().HasDefaultValue("local");
            e.HasIndex(x => new { x.TenantId, x.ContentHash });
            e.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<DocumentPage>(e =>
        {
            e.Property(x => x.Text).HasColumnType("nvarchar(max)");
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.DocumentVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DocumentVersionId, x.PageNumber }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<DocumentProcessingJob>(e =>
        {
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.Status, x.CreatedUtc });
            e.HasIndex(x => new { x.TenantId, x.DocumentVersionId });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<AIUsageLog>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
            e.Property(x => x.Operation).HasMaxLength(100);
            e.Property(x => x.Purpose).HasMaxLength(100);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.EmbeddingUnits).HasPrecision(18, 4);
            e.Property(x => x.EstimatedCostUsd).HasPrecision(18, 6);
            e.HasIndex(x => new { x.Provider, x.TimestampUtc });
            e.HasIndex(x => new { x.TenantId, x.TimestampUtc });
            e.HasIndex(x => new { x.BotLinkId, x.TimestampUtc });
        });

        // Cross-references between knowledge rows (module/page/step ids) are plain columns, not FKs, so that
        // re-analysis and review never hit multi-path cascade conflicts; the owned children below do have FKs.
        ConfigureKnowledge<Module>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => new { x.ApplicationId, x.NormalizedName });
        });
        ConfigureKnowledge<AppPage>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Purpose).HasMaxLength(2000);
            e.HasIndex(x => new { x.ApplicationId, x.NormalizedName });
        });
        ConfigureKnowledge<PageFunctionality>(b, e =>
        {
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.HasOne<AppPage>().WithMany().HasForeignKey(x => x.AppPageId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureKnowledge<ValidationRule>(b, e =>
        {
            e.Property(x => x.FieldName).HasMaxLength(200);
            e.Property(x => x.RuleText).HasMaxLength(2000).IsRequired();
            e.HasOne<AppPage>().WithMany().HasForeignKey(x => x.AppPageId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureKnowledge<BusinessRule>(b, e => e.Property(x => x.Statement).HasMaxLength(2000).IsRequired());
        ConfigureKnowledge<RolePermission>(b, e =>
        {
            e.Property(x => x.Role).HasMaxLength(200).IsRequired();
            e.Property(x => x.Permission).HasMaxLength(500).IsRequired();
        });
        ConfigureKnowledge<PageLink>(b, e =>
        {
            e.Property(x => x.Condition).HasMaxLength(500);
            e.HasIndex(x => x.FromPageId);
        });
        ConfigureKnowledge<Workflow>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => new { x.ApplicationId, x.NormalizedName });
        });
        ConfigureKnowledge<WorkflowStep>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureKnowledge<WorkflowTransition>(b, e =>
        {
            e.Property(x => x.Condition).HasMaxLength(500);
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AnalysisCacheEntry>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(50);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasMaxLength(50);
            e.Property(x => x.ResponseJson).HasColumnType("nvarchar(max)");
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<BotLink>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(120).IsRequired();
            e.Property(x => x.Welcome).HasMaxLength(500);
            e.Property(x => x.StartersJson).HasMaxLength(1500);
            e.Property(x => x.AccentColor).HasMaxLength(7);
            e.Property(x => x.DocumentIdsJson).HasMaxLength(4000);
            e.Property(x => x.ThemeJson).HasMaxLength(4000);
            e.HasOne<Audience>().WithMany().HasForeignKey(x => x.AudienceId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TokenCipher).HasMaxLength(1000);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ApplicationId });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<ChatCacheEntry>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Answer).HasColumnType("nvarchar(max)");
            e.Property(x => x.CitationsJson).HasColumnType("nvarchar(max)");
            e.HasIndex(x => new { x.TenantId, x.CreatedUtc });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<TenantAccessKey>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(16);
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyCipher).HasMaxLength(600);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.HasOne<Audience>().WithMany().HasForeignKey(x => x.AudienceId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<Conversation>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.OwnerId, x.ApplicationId, x.UpdatedUtc });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });
        b.Entity<ConversationMessage>(e =>
        {
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.Content).HasColumnType("nvarchar(max)");
            e.Property(x => x.CitationsJson).HasColumnType("nvarchar(max)");
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConversationId, x.Id });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<QuestionGapStatus>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Key }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<SupportRequest>(e =>
        {
            e.Property(x => x.Channel).HasMaxLength(200).IsRequired();
            e.Property(x => x.Question).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Note).HasMaxLength(600);
            e.Property(x => x.Contact).HasMaxLength(150);
            e.Property(x => x.SenderHash).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.Resolution).HasMaxLength(600);
            e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Status, x.CreatedUtc });
            e.HasIndex(x => new { x.TenantId, x.SenderHash, x.CreatedUtc });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<UserAccount>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
        });

        b.Entity<AccountToken>(e =>
        {
            e.Property(x => x.Purpose).HasMaxLength(10).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AllowedFolder>(e =>
        {
            e.Property(x => x.Path).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.Path).IsUnique();
        });

        b.Entity<StorageSetting>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(10).IsRequired();
            e.Property(x => x.RemoteType).HasMaxLength(10).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(300).IsRequired();
            e.Property(x => x.Bucket).HasMaxLength(100).IsRequired();
            e.Property(x => x.Region).HasMaxLength(50).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(120).IsRequired();
            e.Property(x => x.FolderPath).HasMaxLength(400).IsRequired().HasDefaultValue("");
            e.Property(x => x.AccessKeyCipher).HasMaxLength(600);
            e.Property(x => x.SecretKeyCipher).HasMaxLength(900);
            e.Property(x => x.AccessKeyHint).HasMaxLength(8);
            e.Property(x => x.LastTestMessage).HasMaxLength(300);
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<Audience>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Aliases).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(300);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<AccessRule>(e =>
        {
            e.Property(x => x.TargetKind).HasMaxLength(10).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.TargetKind, x.TargetId }).IsUnique();
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<AccessRuleAudience>(e =>
        {
            e.HasKey(x => new { x.RuleId, x.AudienceId });
            e.HasOne<AccessRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            // No cascade from Audience: deleting an audience is refused while anything uses it, and rule rows are removed explicitly.
            e.HasOne<Audience>().WithMany().HasForeignKey(x => x.AudienceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.AudienceId });
            e.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => new { x.TenantId, x.TimestampUtc });
        });
    }

    // Write guard: a tenant-scoped context can never add or modify another tenant's rows.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenantOnWrites()
    {
        if (CurrentTenantId is not { } tenantId) return;
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity.TenantId != tenantId)
                throw new InvalidOperationException("Cross-tenant write blocked.");
        }
    }
}
