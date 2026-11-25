using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowMind.Tests;

public class TenantIsolationTests
{
    private sealed class FixedTenant : ITenantContext { public FixedTenant(Guid? id) => TenantId = id; public Guid? TenantId { get; } }

    private static FlowMindDbContext Ctx(string db, Guid? tenant) =>
        new(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(db).Options, new FixedTenant(tenant));

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static string Seed()
    {
        var name = Guid.NewGuid().ToString();
        using var db = Ctx(name, null); // unscoped, like the worker
        db.Documents.AddRange(
            new Document { TenantId = A, ApplicationId = Guid.NewGuid(), FileName = "a.pdf" },
            new Document { TenantId = B, ApplicationId = Guid.NewGuid(), FileName = "b.pdf" },
            new Document { TenantId = A, ApplicationId = Guid.NewGuid(), FileName = "deleted.pdf", DeletedUtc = DateTime.UtcNow });
        db.DocumentPages.AddRange(
            new DocumentPage { TenantId = A, DocumentVersionId = Guid.NewGuid(), PageNumber = 1 },
            new DocumentPage { TenantId = B, DocumentVersionId = Guid.NewGuid(), PageNumber = 1 });
        db.SaveChanges();
        return name;
    }

    [Fact]
    public void Tenant_only_sees_its_own_non_deleted_rows()
    {
        var name = Seed();
        using var a = Ctx(name, A);
        using var b = Ctx(name, B);

        Assert.Equal(new[] { "a.pdf" }, a.Documents.Select(d => d.FileName).ToArray());
        Assert.Equal(new[] { "b.pdf" }, b.Documents.Select(d => d.FileName).ToArray());
        Assert.Equal(1, a.DocumentPages.Count());
        Assert.Equal(B, b.DocumentPages.Single().TenantId);
    }

    [Fact]
    public void Direct_lookup_by_id_of_another_tenants_row_returns_nothing()
    {
        var name = Seed();
        Guid bDoc;
        using (var admin = Ctx(name, null)) bDoc = admin.Documents.Single(d => d.FileName == "b.pdf").Id;

        using var a = Ctx(name, A);
        Assert.Null(a.Documents.FirstOrDefault(d => d.Id == bDoc));
    }

    [Fact]
    public void Scoped_context_cannot_write_rows_for_another_tenant()
    {
        var name = Seed();
        using var a = Ctx(name, A);
        a.Documents.Add(new Document { TenantId = B, ApplicationId = Guid.NewGuid(), FileName = "evil.pdf" });
        Assert.Throws<InvalidOperationException>(() => a.SaveChanges());
    }

    [Fact]
    public void Unscoped_context_is_used_by_worker_and_sees_all_tenants()
    {
        var name = Seed();
        using var worker = Ctx(name, null);
        Assert.Equal(2, worker.Documents.Count()); // soft-deleted still hidden
    }
}
