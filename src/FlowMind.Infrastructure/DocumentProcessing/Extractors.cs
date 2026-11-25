using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FlowMind.Infrastructure.DocumentProcessing;

public sealed record ExtractedPage(int PageNumber, string Text, bool HasImages, bool NeedsOcr);

public interface IDocumentTextExtractor
{
    bool CanHandle(string extension);
    IReadOnlyList<ExtractedPage> Extract(Stream content);
}

/// <summary>Digital PDF text via PdfPig. A page with (almost) no text but images is flagged as scanned → needs OCR.</summary>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    private readonly ProcessingOptions _opt;
    public PdfTextExtractor(IOptions<ProcessingOptions> opt) => _opt = opt.Value;

    public bool CanHandle(string extension) => extension.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedPage> Extract(Stream content)
    {
        try
        {
            using var doc = PdfDocument.Open(content);
            if (doc.NumberOfPages > _opt.MaxPages)
                throw new UnreadableDocumentException($"PDF has {doc.NumberOfPages} pages; the limit is {_opt.MaxPages}.");

            var pages = new List<ExtractedPage>();
            foreach (var page in doc.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                var hasImages = page.GetImages().Any();
                var scanned = text.Length < _opt.OcrMinChars && hasImages;
                pages.Add(new ExtractedPage(page.Number, text, hasImages, scanned));
            }
            return pages;
        }
        catch (UnreadableDocumentException) { throw; }
        catch (Exception ex)
        {
            throw new UnreadableDocumentException("The PDF could not be read (corrupt, encrypted or unsupported).", ex);
        }
    }
}

/// <summary>
/// DOCX text with headings and tables. DOCX has no fixed pagination: pages follow Word's saved page breaks
/// (lastRenderedPageBreak) when present, otherwise explicit page breaks, otherwise the document is one page.
/// </summary>
/// <summary>Limits on what an uploaded OOXML (zip) file may unpack to. Uploaded documents are untrusted input.</summary>
internal static class ZipGuard
{
    public const long MaxUnpackedBytes = 100L * 1024 * 1024;
    public const int MaxEntries = 5000;

    public static void Check(Stream s, long maxUnpackedBytes = MaxUnpackedBytes)
    {
        if (!s.CanSeek) return;
        var start = s.Position;
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxEntries) throw new UnreadableDocumentException("The document contains too many parts.");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (total > maxUnpackedBytes) throw new UnreadableDocumentException("The document is too large once unpacked.");
            }
        }
        catch (InvalidDataException) { throw new UnreadableDocumentException("The file is not a valid DOCX document."); }
        finally { s.Position = start; }
    }
}

public sealed class DocxTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string extension) => extension.Equals("docx", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedPage> Extract(Stream content)
    {
        ZipGuard.Check(content); // a small .docx can unpack to gigabytes; refuse before OpenXml touches it
        try
        {
            using var doc = WordprocessingDocument.Open(content, false);
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new UnreadableDocumentException("The DOCX has no body.");

            var useRendered = body.Descendants<LastRenderedPageBreak>().Any();
            var pages = new List<(StringBuilder Text, bool Images)> { (new StringBuilder(), false) };

            void NewPage() => pages.Add((new StringBuilder(), false));
            void Append(string line)
            {
                if (line.Length == 0) return;
                pages[^1].Text.AppendLine(line);
            }

            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph p:
                        AppendParagraph(p, useRendered, NewPage, Append, () => pages[^1] = (pages[^1].Text, true));
                        break;
                    case Table t:
                        foreach (var row in t.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>().Select(c => string.Join(" ", c.Descendants<Text>().Select(x => x.Text)).Trim());
                            Append("| " + string.Join(" | ", cells) + " |");
                        }
                        break;
                }
            }

            return pages
                .Select((p, i) => new ExtractedPage(i + 1, p.Text.ToString().Trim(), p.Images, false))
                .Where(p => p.Text.Length > 0 || p.HasImages)
                .ToList() is { Count: > 0 } result
                    ? result
                    : new List<ExtractedPage> { new(1, "", false, false) };
        }
        catch (UnreadableDocumentException) { throw; }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException or IOException)
        {
            throw new UnreadableDocumentException("The DOCX could not be read (corrupt or unsupported).", ex);
        }
    }

    private static void AppendParagraph(Paragraph p, bool useRendered, Action newPage, Action<string> append, Action markImage)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case LastRenderedPageBreak when useRendered:
                    Flush(); newPage(); break;
                case Break br when !useRendered && br.Type?.Value == BreakValues.Page:
                    Flush(); newPage(); break;
                case Text t: sb.Append(t.Text); break;
                case TabChar: sb.Append('\t'); break;
                case Drawing: markImage(); break;
            }
        }
        Flush();

        void Flush()
        {
            var text = sb.ToString().Trim();
            sb.Clear();
            if (text.Length == 0) return;
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (style != null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(style.AsSpan("Heading".Length), out var level) && level is >= 1 and <= 6)
                text = new string('#', level) + " " + text;
            append(text);
        }
    }
}

/// <summary>Plain text. A form-feed (\f) starts a new page; otherwise the file is a single page.</summary>
public sealed class TxtTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string extension) => extension.Equals("txt", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedPage> Extract(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new UnreadableDocumentException("The file is not a text file.");
        return text.Split('\f')
            .Select((t, i) => new ExtractedPage(i + 1, t.Trim(), false, false))
            .ToList();
    }
}
