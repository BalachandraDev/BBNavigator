using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;

namespace FlowMind.Application.Interfaces;

/// <summary>Tenant of the current execution scope, taken from authenticated claims (never from request input).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    /// <summary>Token subject of the signed-in caller (null for background work). Default keeps existing implementations valid.</summary>
    string? UserId => null;
}

public sealed record StagedFile(string TempPath, string Sha256, long SizeBytes);
/// <param name="Path">Relative path (same shape for every location): tenant/document/vN/original.ext</param>
/// <param name="Kind">"local" (this server's folder) or "remote" (the company's own bucket from Settings).</param>
public sealed record StoredFile(string Path, string Kind);

/// <summary>The storage location is chosen per company (Settings > Storage). New files go to the company's active location; each stored file remembers its own.</summary>
public interface IFileStorage
{
    /// <summary>Copies the stream to a private temp file on this server while computing its SHA-256. Enforces the size cap.</summary>
    Task<StagedFile> StageAsync(Stream content, long maxBytes, CancellationToken ct);
    /// <summary>Moves a staged file to the company's active location and says where it ended up.</summary>
    Task<StoredFile> CommitAsync(StagedFile staged, Guid tenantId, Guid documentId, int versionNumber, string extension, CancellationToken ct);
    void Discard(StagedFile staged);
    /// <summary>Opens a stored file for reading (always seekable).</summary>
    Task<Stream> OpenReadAsync(string path, string kind, Guid tenantId, CancellationToken ct);
    /// <summary>Copies a stored file to another location after checking its SHA-256. The source is left in place.</summary>
    Task<StoredFile> CopyAsync(string path, string fromKind, string toKind, Guid tenantId, string expectedSha256, CancellationToken ct);
    /// <summary>Removes one stored file from one location (used after a verified copy, when the admin asks for it).</summary>
    Task RemoveAsync(string path, string kind, Guid tenantId, CancellationToken ct);
    /// <summary>Remove a document's files from every location the company uses.</summary>
    Task DeleteDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct);
    /// <summary>Removes every stored file of a company, from every location it uses.</summary>
    Task DeleteTenantAsync(Guid tenantId, CancellationToken ct);
    Task DeleteVersionAsync(Guid tenantId, Guid documentId, int versionNumber, CancellationToken ct);
}
/// <summary>
/// Who a request's AI calls are for. The public share-link endpoint sets <see cref="BotLinkId"/> from the validated link (never from the visitor),
/// and every usage record written during that request (chat answer, query embedding, retries) is stamped with it automatically.
/// Left empty for admin chat, access keys and document processing.
/// </summary>
public sealed class UsageAttribution
{
    public Guid? BotLinkId { get; set; }
}

public interface IAiUsageService
{
    /// <summary>
    /// Throws <see cref="QuotaExceededException"/> when the configured daily limit would be exceeded
    /// (or is not configured). Never falls back to a paid model.
    /// </summary>
    Task EnsureWithinLimitAsync(AiProviderKind kind, string provider, int requestedUnits, CancellationToken ct);
    Task RecordAsync(AIUsageLog log, CancellationToken ct);
}

public interface IAuditLogger
{
    Task LogAsync(string action, string? entityType = null, string? entityId = null, string? detail = null,
        Guid? tenantId = null, CancellationToken ct = default);
}

/// <summary>The provider cannot be used because it is not configured (e.g. missing API key).</summary>
public sealed class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException(string provider, string message) : base(message) => Provider = provider;
    public string Provider { get; }
}

/// <summary>Temporary provider failure (5xx, rate limit). Safe to retry with backoff.</summary>
public sealed class TransientProviderException : Exception
{
    public TransientProviderException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The AI returned output that is empty, truncated, blocked or does not match the expected structure.</summary>
public sealed class InvalidAnalysisOutputException : Exception
{
    public InvalidAnalysisOutputException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The uploaded document is corrupt, encrypted, unsupported or exceeds limits. Retrying will not help.</summary>
public sealed class UnreadableDocumentException : Exception
{
    public UnreadableDocumentException(string message, Exception? inner = null) : base(message, inner) { }
}
