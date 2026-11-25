using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Storage;

/// <summary>Any S3-compatible bucket: Cloudflare R2, MinIO, or a company's own cloud. Path-style addressing works with all of them.</summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly bool _https;

    public S3ObjectStore(StorageConnection c)
    {
        var cfg = new AmazonS3Config
        {
            ServiceURL = c.Endpoint, ForcePathStyle = true, AuthenticationRegion = string.IsNullOrWhiteSpace(c.Region) ? "auto" : c.Region,
            Timeout = TimeSpan.FromSeconds(60), MaxErrorRetry = 2,
            // Newer SDK defaults add checksum headers that some S3-compatible services reject; only send them when the service requires them.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(c.AccessKey, c.SecretKey), cfg);
        _bucket = c.Bucket; _https = c.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public async Task PutFileAsync(string key, string filePath, CancellationToken ct) =>
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, FilePath = filePath, UseChunkEncoding = false, DisablePayloadSigning = _https }, ct);

    public async Task<Stream> GetAsync(string key, CancellationToken ct)
    {
        using var res = await _s3.GetObjectAsync(_bucket, key, ct);
        var ms = new MemoryStream();
        await res.ResponseStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task DeleteAsync(string key, CancellationToken ct) => await _s3.DeleteObjectAsync(_bucket, key, ct);

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        string? token = null;
        do
        {
            var list = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, ct);
            if (list.S3Objects.Count > 0)
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = list.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList() }, ct);
            token = list.IsTruncated ? list.NextContinuationToken : null;
        } while (token != null);
    }

    public async Task<StorageProbeResult> ProbeAsync(string keyPrefix, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = keyPrefix + "_flowmind-probe-" + Guid.NewGuid().ToString("N") + ".txt";
        try
        {
            var tmp = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tmp, "flowmind connection test", ct);
                await PutFileAsync(key, tmp, ct);
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
            await using (var back = await GetAsync(key, ct)) { if (back.Length == 0) return new(false, "The bucket accepted the test file but returned it empty.", sw.ElapsedMilliseconds); }
            await DeleteAsync(key, ct);
            return new(true, "Connected. A test file was written, read back and removed.", sw.ElapsedMilliseconds);
        }
        catch (AmazonS3Exception ex) { return new(false, Explain(ex), sw.ElapsedMilliseconds); }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or WebException or TaskCanceledException or IOException)
        { return new(false, "Could not reach the storage address. Check the address and that it is online.", sw.ElapsedMilliseconds); }
    }

    private static string Explain(AmazonS3Exception ex) => ex.ErrorCode switch
    {
        "InvalidAccessKeyId" or "InvalidAccessKey" => "The access key id was not recognised. Check it for typing mistakes.",
        "SignatureDoesNotMatch" => "The secret access key does not match the access key id.",
        "NoSuchBucket" => "That bucket does not exist. Check the bucket name.",
        "AccessDenied" or "Forbidden" => "The key is valid but is not allowed to read and write in this bucket. Give it object read and write permission.",
        "RequestTimeTooSkewed" => "The server clock is out of step with the storage service. Check this server's date and time.",
        _ when ex.StatusCode == HttpStatusCode.NotFound => "The storage address or bucket was not found. Check both.",
        _ => $"The storage service refused the request ({(int)ex.StatusCode}{(string.IsNullOrEmpty(ex.ErrorCode) ? "" : ", " + ex.ErrorCode)}).",
    };

    public void Dispose() => _s3.Dispose();
}

public sealed class S3ObjectStoreFactory : IObjectStoreFactory
{
    private readonly bool _allowPrivate;
    public S3ObjectStoreFactory(IOptions<StorageOptions> options) { _allowPrivate = options.Value.AllowPrivateEndpoints; }

    public IObjectStore Create(StorageConnection connection)
    {
        // Checked again here (not only when saving) so an address that later starts resolving to a private one is never contacted.
        if (EndpointGuard.Check(connection.Endpoint, _allowPrivate) is { } problem) throw new StorageUnavailableException(problem);
        return new S3ObjectStore(connection);
    }
}