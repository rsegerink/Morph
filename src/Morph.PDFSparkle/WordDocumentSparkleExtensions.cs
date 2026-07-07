namespace Morph;

/// <summary>Adds ExportToSparklePdf methods to WordDocument.</summary>
public static class WordDocumentSparkleExtensions
{
    public static byte[] ExportToSparklePdf(this WordDocument document, PdfExportOptions? options = null) =>
        SparkleRenderer.Render(document.Document, options);

    public static void ExportToSparklePdf(this WordDocument document, string outputPdfPath, PdfExportOptions? options = null) =>
        System.IO.File.WriteAllBytes(outputPdfPath, document.ExportToSparklePdf(options));

    public static void ExportToSparklePdf(this WordDocument document, System.IO.Stream output, PdfExportOptions? options = null)
    {
        var bytes = document.ExportToSparklePdf(options);
        output.Write(bytes, 0, bytes.Length);
    }
}
