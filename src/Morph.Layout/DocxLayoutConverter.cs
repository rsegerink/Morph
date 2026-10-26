using DocumentFormat.OpenXml.Packaging;

namespace Morph.Layout;

/// <summary>
/// Converts a DOCX to PDF the way <see cref="PdfDocumentConverter"/> does, and returns the word
/// positions from the same layout pass, so they match the PDF exactly.
/// </summary>
public static class DocxLayoutConverter
{
    public static DocxLayoutResult Convert(string docxPath, PdfExportOptions? options = null, Cancel cancel = default)
    {
        using var stream = File.OpenRead(docxPath);
        return Convert(stream, options, cancel);
    }

    // Mirrors PdfDocumentConverter.ConvertToPdf and PdfRenderer.Render step for step; only the walk over
    // the laid-out tree is added, against the painter's own context so words resolve the drawn faces.
    // captureSources stamps each paragraph's w:p ordinal (LayoutParagraph.Source); layout never reads it.
    // The package is opened here, as DocumentParser.Parse(Stream) would, so the outline levels the parser
    // does not keep are read from the same tree before it parses.
    public static DocxLayoutResult Convert(Stream docxStream, PdfExportOptions? options = null, Cancel cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        options ??= new();
        var normalized = StrictToTransitional.Normalize(docxStream);
        try
        {
            using var package = WordprocessingDocument.Open(normalized, false);
            var outlineLevels = OutlineLevels.Of(package);
            var document = new DocumentParser(options.DefaultFont ?? DefaultFontSettings.CustomizedDefaultFont, captureSources: true, culture: options.Culture).Parse(package);
            return Convert(document, outlineLevels, options, cancel);
        }
        finally
        {
            if (!ReferenceEquals(normalized, docxStream))
            {
                normalized.Dispose();
            }
        }
    }

    static DocxLayoutResult Convert(ParsedDocument document, Dictionary<int, int> outlineLevels, PdfExportOptions options, Cancel cancel)
    {
        using var fontResolver = LayoutFonts.CreateResolver(options.FontDirectory, options.FontFallback);
        var measurer = new CanonicalParagraphMeasurer(LayoutFonts.ToDelegate(fontResolver), options.FontWidthScale, document.Compatibility.SqueezesJustifiedLines);
        var laidOut = new Fragmenter(measurer, document.Compatibility)
            .Layout(
                document.Elements,
                document.PageSettings,
                document.Header,
                document.Footer,
                document.FirstPageHeader,
                document.FirstPageFooter,
                document.EvenPageHeader,
                document.EvenPageFooter,
                DocumentNotes.From(document))
            .WithComments(document, measurer, options.ShowComments);
        cancel.ThrowIfCancellationRequested();

        using var context = PdfPainter.NewContext(laidOut, document.Compatibility, options.FontWidthScale, options.FontFallback, options.FontDirectory);
        PdfPainter.Paint(laidOut, context, options.RasterizeWordArt, options.OnWarning, options.Pages, cancel);
        var layout = new LayoutWalker(context, document, outlineLevels).Walk(laidOut, options.Pages);
        var pdf = context.Document.Finish();
        return new(pdf, layout);
    }
}
