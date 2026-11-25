using System.Text;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.DocumentProcessing;
using FlowMind.Infrastructure.Retrieval;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowMind.Tests;

/// <summary>"Add update": a small change is appended to an uploaded document; only the new text enters the pipeline.</summary>
public class DocumentUpdateTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();

    private static FlowMindDbContext NewDb() => new(
        new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());

    private static DocumentUpdateService Service(FlowMindDbContext db) => new(db, new IDocumentTextExtractor[] { new TxtTextExtractor() });

    /// <summary>An approved, indexed document with 3 pages and no running jobs.</summary>
    private static (Document doc, DocumentVersion ver) Seed(FlowMindDbContext db, DocumentStatus status = DocumentStatus.Approved)
    {
        var doc = new Document { TenantId = Tenant, ApplicationId = App, FileName = "manual.pdf", ContentType = "application/pdf", Status = status };
        var ver = new DocumentVersion { TenantId = Tenant, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h", StoragePath = "p", PageCount = 3 };
        doc.CurrentVersionId = ver.Id;
        db.Documents.Add(doc); db.DocumentVersions.Add(ver);
        for (var i = 1; i <= 3; i++) db.DocumentPages.Add(new DocumentPage { TenantId = Tenant, DocumentVersionId = ver.Id, PageNumber = i, Text = $"Old page {i} text about discharge.", Source = PageTextSource.Digital, CharCount = 30 });
        db.SaveChanges();
        return (doc, ver);
    }

    [Fact]
    public async Task Pasted_text_is_appended_as_dated_pages_and_queued_for_analysis_only()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db);
        var r = await Service(db).AddTextAsync(doc.Id, "Discharge changes", "The discharge screen now has an Insurance tab.\n\nFill it before printing.", default);

        Assert.Equal(1, r.AddedPages); Assert.Equal(4, r.TotalPages);
        var pages = db.DocumentPages.Where(p => p.DocumentVersionId == ver.Id).OrderBy(p => p.PageNumber).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, pages.Select(p => p.PageNumber).ToArray());
        Assert.Equal("Old page 1 text about discharge.", pages[0].Text);                       // old pages untouched
        Assert.StartsWith("UPDATE ", pages[3].Text);
        Assert.Contains("Discharge changes", pages[3].Text);
        Assert.Contains("Insurance tab", pages[3].Text);
        Assert.Equal(4, db.DocumentVersions.Single().PageCount);
        Assert.Equal(DocumentStatus.Extracted, db.Documents.Single().Status);                 // back to the analysis step
        var job = Assert.Single(db.ProcessingJobs);
        Assert.Equal(JobStage.Analysis, job.Stage); Assert.Equal(JobStatus.Pending, job.Status); Assert.Equal(ver.Id, job.DocumentVersionId);
        Assert.Single(db.DocumentVersions);                                                    // no second copy of the document
    }

    [Fact]
    public async Task Old_pages_chunk_exactly_as_before_so_only_the_update_is_embedded()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db);
        (int, string)[] Pages() => db.DocumentPages.Where(p => p.DocumentVersionId == ver.Id).OrderBy(p => p.PageNumber).AsEnumerable().Select(p => (p.PageNumber, p.Text)).ToArray();

        var before = Chunker.Build(Pages(), 900, 100);
        await Service(db).AddTextAsync(doc.Id, "Change", "Brand new rule: refunds need a manager approval before printing.", default);
        var after = Chunker.Build(Pages(), 900, 100);

        Assert.True(after.Count > before.Count);
        Assert.Equal(before.Select(c => (c.Index, c.Hash)), after.Take(before.Count).Select(c => (c.Index, c.Hash)));   // same index + hash = reused by the indexer, not re-embedded
    }

    [Fact]
    public async Task Long_text_is_split_into_pages_that_each_carry_the_UPDATE_marker()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db);
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Paragraph {i}: " + new string('x', 900)));
        var r = await Service(db).AddTextAsync(doc.Id, "Big change", text, default);

        Assert.True(r.AddedPages >= 3);
        var added = db.DocumentPages.Where(p => p.DocumentVersionId == ver.Id && p.PageNumber > 3).OrderBy(p => p.PageNumber).ToList();
        Assert.All(added, p => { Assert.StartsWith("UPDATE ", p.Text); Assert.True(p.Text.Length < 4000); });
        Assert.Contains("(part 1 of", added[0].Text);
    }

    [Fact]
    public async Task A_small_file_is_appended_too_and_scanned_or_empty_or_big_ones_are_refused()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db);
        await using (var s = new MemoryStream(Encoding.UTF8.GetBytes("Registration now asks for a mobile number.")))
            Assert.Equal(1, (await Service(db).AddFileAsync(doc.Id, "Registration", s, "txt", default)).AddedPages);
        Assert.Contains("mobile number", db.DocumentPages.Single(p => p.PageNumber == 4).Text);

        db.ProcessingJobs.RemoveRange(db.ProcessingJobs); db.Documents.Single().Status = DocumentStatus.Approved; db.SaveChanges();
        await using var empty = new MemoryStream(Encoding.UTF8.GetBytes("   \n  "));
        var e = await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddFileAsync(doc.Id, "x", empty, "txt", default));
        Assert.Equal(400, e.StatusCode);
        await using var other = new MemoryStream(new byte[] { 1 });
        Assert.Equal(415, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddFileAsync(doc.Id, "x", other, "exe", default))).StatusCode);
    }

    [Fact]
    public async Task Updates_are_refused_while_the_document_is_busy_missing_or_the_text_is_unusable()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db, DocumentStatus.Analyzing);
        Assert.Equal(409, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddTextAsync(doc.Id, "t", "text", default))).StatusCode);

        doc.Status = DocumentStatus.Approved; db.SaveChanges();
        db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = Tenant, DocumentVersionId = ver.Id, Stage = JobStage.Embedding, Status = JobStatus.Running }); db.SaveChanges();
        Assert.Equal(409, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddTextAsync(doc.Id, "t", "text", default))).StatusCode);   // indexing in progress

        db.ProcessingJobs.RemoveRange(db.ProcessingJobs); db.SaveChanges();
        Assert.Equal(404, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddTextAsync(Guid.NewGuid(), "t", "text", default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddTextAsync(doc.Id, "t", "  \u0000 ", default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DocumentUpdateRejectedException>(() => Service(db).AddTextAsync(doc.Id, "t", new string('a', DocumentUpdateService.MaxTextChars + 1), default))).StatusCode);
        Assert.Equal(3, db.DocumentPages.Count());                                            // nothing was added by any refused call
        Assert.Empty(db.ProcessingJobs);
    }

    [Fact]
    public async Task A_pending_review_document_can_take_an_update_and_control_characters_are_removed()
    {
        using var db = NewDb();
        var (doc, ver) = Seed(db, DocumentStatus.PendingReview);
        await Service(db).AddTextAsync(doc.Id, "Ti\u0007tle\nwith break", "Line one\u0000\r\nline two\u001b", default);
        var text = db.DocumentPages.Single(p => p.PageNumber == 4).Text;
        Assert.DoesNotContain('\u0000', text); Assert.DoesNotContain('\u001b', text); Assert.DoesNotContain('\u0007', text);
        Assert.Contains("Line one\nline two", text);
    }
}
