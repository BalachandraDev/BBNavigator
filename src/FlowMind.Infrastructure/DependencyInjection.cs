using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.DocumentProcessing;
using FlowMind.Infrastructure.Knowledge;
using FlowMind.Infrastructure.Retrieval;
using Microsoft.Extensions.Options;
using FlowMind.Infrastructure.Storage;
using FlowMind.Infrastructure.VectorStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowMind.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Call AFTER the host has registered its own ITenantContext (the API does); the worker has none and
    /// gets an unscoped context, which is correct for system-level background processing.
    /// </summary>
    public static IServiceCollection AddFlowMindInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var sql = config.GetConnectionString("SqlServer");
        var pg = config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("ConnectionStrings:SqlServer is not configured.");
        if (string.IsNullOrWhiteSpace(pg)) throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.TryAddSingleton<ITenantContext, UnscopedTenantContext>();

        services.AddDbContext<FlowMindDbContext>(o => o.UseSqlServer(sql, s => s.EnableRetryOnFailure()));

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<ProcessingOptions>(config.GetSection(ProcessingOptions.Section));
        services.Configure<AiProvidersOptions>(config.GetSection(AiProvidersOptions.Section));

        services.AddSingleton(new FlowMind.Infrastructure.Security.SecretBox(config["Security:SecretsKey"]));   // also used by the worker to read cloud storage credentials
        services.AddSingleton<LocalFileStorage>();
        services.AddSingleton<IObjectStoreFactory, S3ObjectStoreFactory>();
        services.AddScoped<FolderRoots>();
        services.Configure<FlowMind.Infrastructure.Accounts.AccountOptions>(config.GetSection(FlowMind.Infrastructure.Accounts.AccountOptions.Section));
        services.Configure<FlowMind.Infrastructure.Accounts.EmailOptions>(config.GetSection(FlowMind.Infrastructure.Accounts.EmailOptions.Section));
        services.AddSingleton<FlowMind.Infrastructure.Accounts.IEmailSender, FlowMind.Infrastructure.Accounts.SmtpEmailSender>();
        services.AddScoped<FlowMind.Infrastructure.Accounts.AccountService>();
        services.AddScoped<FlowMind.Infrastructure.Accounts.AccountMailer>();
        services.AddScoped<IFileStorage, RoutedFileStorage>();
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, TxtTextExtractor>();

        services.Configure<AnalysisOptions>(config.GetSection(AnalysisOptions.Section));
        services.AddSingleton<ProviderThrottle>();
        services.AddHttpClient<IOcrProvider, MistralOcrProvider>(c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddHttpClient<IDocumentAnalysisProvider, GeminiAnalysisProvider>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.Configure<RetrievalOptions>(config.GetSection(RetrievalOptions.Section));
        services.AddHttpClient<IEmbeddingProvider, CloudflareEmbeddingProvider>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton<IVectorStore>(sp => new PgVectorStore(pg, sp.GetRequiredService<IOptions<AiProvidersOptions>>()));
        services.Configure<ChatOptions>(config.GetSection(ChatOptions.Section));
        services.AddHttpClient<IChatProvider, GroqChatProvider>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient<IMultilingualChatProvider, GeminiChatProvider>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddScoped<Chat.ChatService>();
        services.AddScoped<EmbeddingService>();
        services.AddScoped<DocumentIndexer>();
        services.AddScoped<RetrievalService>();
        services.AddScoped<KnowledgeWriter>();
        services.AddScoped<DocumentAnalyzer>();
        services.AddScoped<UsageAttribution>();
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.AddScoped<AiLimitsReport>();
        services.AddScoped<DocumentProcessor>();
        services.AddScoped<DocumentUpdateService>();
        services.AddScoped<FlowMind.Infrastructure.Insights.InsightsService>();
        services.AddScoped<FlowMind.Infrastructure.Insights.SupportService>();
        services.AddScoped<FlowMind.Infrastructure.Access.AccessResolver>();      // who may see which pages (needs IMemoryCache: registered by the API host)
        services.AddScoped<FlowMind.Infrastructure.Access.AccessAdminService>();
        services.AddScoped<DocumentJobRunner>();

        services.AddHealthChecks()
            .AddDbContextCheck<FlowMindDbContext>("sqlserver", tags: new[] { "ready" })
            .AddCheck("postgres-pgvector", new PostgresVectorHealthCheck(pg), tags: new[] { "ready" });

        return services;
    }
}

/// <summary>No tenant scope (background worker, design-time tools).</summary>
public sealed class UnscopedTenantContext : ITenantContext
{
    public Guid? TenantId => null;
}
