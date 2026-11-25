using System.Text;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.DocumentProcessing;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

public class ExtractorTests
{
    private static IOptions<ProcessingOptions> Opts() => Options.Create(new ProcessingOptions());

    [Fact]
    public void Pdf_digital_page_is_not_flagged_scanned_but_image_only_page_is()
    {
        var pages = new PdfTextExtractor(Opts()).Extract(new MemoryStream(TestSupport.MakePdf()));

        Assert.Equal(3, pages.Count);
        Assert.Contains("Patient Registration", pages[0].Text);
        Assert.False(pages[0].NeedsOcr);                 // digital → no OCR spend
        Assert.True(pages[1].NeedsOcr);                  // image only → OCR
        Assert.False(pages[2].NeedsOcr);                 // blank, no images → nothing to OCR
        Assert.Equal(new[] { 1, 2, 3 }, pages.Select(p => p.PageNumber));
    }

    [Fact]
    public void Pdf_garbage_is_reported_as_unreadable()
    {
        Assert.Throws<UnreadableDocumentException>(() =>
            new PdfTextExtractor(Opts()).Extract(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 not really a pdf"))));
    }

    [Fact]
    public void Pdf_over_page_limit_is_rejected()
    {
        var ex = new PdfTextExtractor(Options.Create(new ProcessingOptions { MaxPages = 2 }));
        Assert.Throws<UnreadableDocumentException>(() => ex.Extract(new MemoryStream(TestSupport.MakePdf())));
    }

    [Fact]
    public void Docx_keeps_headings_tables_and_splits_on_page_breaks()
    {
        var pages = new DocxTextExtractor().Extract(new MemoryStream(TestSupport.MakeDocx()));

        Assert.Equal(2, pages.Count);
        Assert.Contains("# Billing Module", pages[0].Text);
        Assert.Contains("| Field | Validation |", pages[0].Text);
        Assert.Contains("Second page content.", pages[1].Text);
        Assert.Equal(2, pages[1].PageNumber);
    }

    [Fact]
    public void Docx_garbage_is_reported_as_unreadable()
    {
        Assert.Throws<UnreadableDocumentException>(() =>
            new DocxTextExtractor().Extract(new MemoryStream(new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3 })));
    }

    /// <summary>Opt-in: FLOWMIND_WRITE_SAMPLES=&lt;dir&gt; writes synthetic sample documents for manual/e2e testing.</summary>
    [Fact]
    public void Write_sample_files_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("FLOWMIND_WRITE_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "sample-registration.pdf"), TestSupport.MakePdf());
        File.WriteAllBytes(Path.Combine(dir, "sample-billing.docx"), TestSupport.MakeDocx());
        File.WriteAllText(Path.Combine(dir, "sample-notes.txt"), "Discharge page\nDischarge date is mandatory.\fSummary page\nPrinted after approval.");
        File.WriteAllText(Path.Combine(dir, "fake.pdf"), "this is not a pdf");
    }

    [Fact]
    public void Txt_splits_on_form_feed_and_rejects_binary()
    {
        var pages = new TxtTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes("page one\fpage two")));
        Assert.Equal(2, pages.Count);
        Assert.Equal("page two", pages[1].Text);

        Assert.Throws<UnreadableDocumentException>(() => new TxtTextExtractor().Extract(new MemoryStream(new byte[] { 65, 0, 66 })));
    }
}
