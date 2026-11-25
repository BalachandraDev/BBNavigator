namespace FlowMind.Infrastructure.Storage;

/// <summary>A decrypted connection to an S3-compatible bucket. Lives only in memory, for the duration of a request.</summary>
public sealed record StorageConnection(string Endpoint, string Bucket, string Region, string Prefix, string AccessKey, string SecretKey);

public sealed record StorageProbeResult(bool Ok, string Message, long Milliseconds);

/// <summary>The configured location cannot be used right now (not set up, wrong keys, unreachable). Shown to the admin as a clear message.</summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Talks to a bucket. Keys are complete object keys; the caller (the router) decides the folder layout and prefix.</summary>
public interface IObjectStore
{
    Task PutFileAsync(string key, string filePath, CancellationToken ct);
    /// <summary>The whole object in a seekable stream (uploads are capped at the upload limit, so this is small).</summary>
    Task<Stream> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task DeletePrefixAsync(string prefix, CancellationToken ct);
    /// <summary>Writes, reads back and deletes a tiny object, and says in plain words what went wrong if it cannot.</summary>
    Task<StorageProbeResult> ProbeAsync(string keyPrefix, CancellationToken ct);
}

public interface IObjectStoreFactory
{
    IObjectStore Create(StorageConnection connection);
}