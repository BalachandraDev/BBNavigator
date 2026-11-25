using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FlowMind.Tests;

public static class TestSupport
{
    public const string SigningKey = "TEST-signing-key-0123456789-abcdefghijkl";

    /// <summary>API under test: in-memory SQL stand-in (JWT validation now consults the database), PostgreSQL deliberately unreachable.</summary>
    public static WebApplicationFactory<Program> Configure(this WebApplicationFactory<Program> f)
    {
        var dbName = "api-tests-" + Guid.NewGuid();
        return f.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:SqlServer", "Server=localhost,1;Database=x;Connect Timeout=1");
            b.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Port=1;Database=x;Timeout=1");
            b.UseSetting("Storage:RootPath", Path.Combine(Path.GetTempPath(), "flowmind-tests"));
            b.UseSetting("Auth:SigningKey", SigningKey);
            b.UseSetting("Auth:PlatformAdminKey", "test-platform-admin-key");
            b.UseSetting("Security:SecretsKey", "TEST-secrets-key-0123456789-abcdefghijklmnop");
            b.ConfigureServices(s =>
            {
                foreach (var d in s.Where(x => x.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<FlowMind.Infrastructure.Database.FlowMindDbContext>)).ToList())
                    s.Remove(d);
                s.AddDbContext<FlowMind.Infrastructure.Database.FlowMindDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    // ---- Synthetic documents (no real data) ----
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>Page 1: digital text. Page 2: image only (scanned). Page 3: blank.</summary>
    public static byte[] MakePdf()
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);

        var p1 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p1.AddText("Patient Registration: the user enters demographics and saves the record.", 12, new PdfPoint(25, 700), font);

        var p2 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p2.AddPng(Convert.FromBase64String(Png1x1), new PdfRectangle(50, 50, 300, 300));

        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        return b.Build();
    }

    public static byte[] MakeDocx(bool withExplicitBreak = true)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text("Billing Module"))));
            body.Append(new Paragraph(new Run(new Text("Invoices are generated after discharge."))));
            body.Append(new Table(new TableRow(
                new TableCell(new Paragraph(new Run(new Text("Field")))),
                new TableCell(new Paragraph(new Run(new Text("Validation")))))));
            if (withExplicitBreak) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            body.Append(new Paragraph(new Run(new Text("Second page content."))));
            main.Document.Save();
        }
        return ms.ToArray();
    }
}

