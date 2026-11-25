using FlowMind.Application.Interfaces;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.DocumentProcessing;

/// <summary>Claims and runs one pending job. Safe against crashes (stale jobs are requeued) and concurrent workers.</summary>
public sealed class DocumentJobRunner
{
    private readonly FlowMindDbContext _db;
    private readonly DocumentProcessor _processor;
    private readonly Knowledge.DocumentAnalyzer _analyzer;
    private readonly Retrieval.DocumentIndexer _indexer;
    private readonly ProcessingOptions _opt;
    private readonly ILogger<DocumentJobRunner> _log;

    public DocumentJobRunner(FlowMindDbContext db, DocumentProcessor processor, Knowledge.DocumentAnalyzer analyzer,
        Retrieval.DocumentIndexer indexer, IOptions<ProcessingOptions> opt, ILogger<DocumentJobRunner> log)
    {
        _db = db; _processor = processor; _analyzer = analyzer; _indexer = indexer; _opt = opt.Value; _log = log;
    }

    /// <returns>true if a job was claimed (so the caller should poll again immediately).</returns>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Requeue jobs left "Running" by a crashed worker.
        var staleBefore = now.AddMinutes(-_opt.StaleJobMinutes);
        await _db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Running && j.StartedUtc < staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Pending), ct);

        var id = await _db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Pending && (j.NextAttemptUtc == null || j.NextAttemptUtc <= now))
            .OrderBy(j => j.CreatedUtc).Select(j => j.Id).FirstOrDefaultAsync(ct);
        if (id == Guid.Empty) return false;

        // Atomic claim: only one worker can flip Pending → Running.
        var claimed = await _db.ProcessingJobs.Where(j => j.Id == id && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedUtc, now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
        if (claimed == 0) return true;

        var job = await _db.ProcessingJobs.FirstAsync(j => j.Id == id, ct);
        try
        {
            if (job.Stage == JobStage.Analysis) await _analyzer.ProcessAsync(job, ct);
            else if (job.Stage == JobStage.Embedding) await _indexer.ProcessAsync(job, ct);
            else await _processor.ProcessAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = JobStatus.Pending; // shutting down: put it back
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (UnreadableDocumentException ex)
        {
            await FailAsync(job, ex.Message, retry: false);
        }
        catch (InvalidAnalysisOutputException ex)
        {
            // Output problems are not auto-retried (each retry spends quota); the user can retry manually.
            _log.LogWarning(ex, "Unusable AI output on job {Job}", job.Id);
            await FailAsync(job, ex.Message, retry: false);
        }
        catch (TransientProviderException ex)
        {
            _log.LogWarning(ex, "Transient failure on job {Job}", job.Id);
            await FailAsync(job, ex.Message, retry: true);
        }
        catch (Exception ex)
        {
            // Unknown failures are not retried automatically: a retry could spend more provider quota.
            _log.LogError(ex, "Job {Job} failed", job.Id);
            await FailAsync(job, "Processing failed unexpectedly. See server logs.", retry: false);
        }
        return true;
    }

    private async Task FailAsync(Domain.Entities.DocumentProcessingJob job, string error, bool retry)
    {
        // The failed operation may have left tracked entities in a bad state; reload what we need.
        _db.ChangeTracker.Clear();
        var j = await _db.ProcessingJobs.FirstAsync(x => x.Id == job.Id);
        j.Error = error.Length > 2000 ? error[..2000] : error;

        if (retry && j.Attempts < _opt.MaxAttempts)
        {
            j.Status = JobStatus.Pending;
            j.NextAttemptUtc = DateTime.UtcNow.AddSeconds(30 * Math.Pow(2, j.Attempts - 1)); // 30s, 60s, 120s…
        }
        else
        {
            j.Status = JobStatus.Failed;
            j.CompletedUtc = DateTime.UtcNow;
            // A failed indexing run must not un-approve the document; the job error tells the admin to re-index.
            if (j.Stage != JobStage.Embedding)
            {
                var version = await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == j.DocumentVersionId);
                var doc = version is null ? null : await _db.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId);
                if (doc != null) doc.Status = DocumentStatus.Failed;
            }
        }
        await _db.SaveChangesAsync();
    }
}
