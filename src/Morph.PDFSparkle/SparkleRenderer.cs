namespace Morph;

/// <summary>
/// Renders a parsed document to a PDF byte array using Sparkle. Output is made byte-reproducible
/// (see <see cref="Normalize"/>) so it can be snapshot-tested, mirroring <c>PdfRenderer</c>.
/// </summary>
public static class SparkleRenderer
{
    internal static byte[] Render(ParsedDocument document, PdfExportOptions? options = null)
    {
        options ??= new();
        var context = new SparkleRenderContext(
            document.PageSettings,
            document.Compatibility,
            options.FontWidthScale,
            options.FontFallback,
            options.FontDirectory);

        var renderer = new SparklePageRenderer(context) { OnWarning = options.OnWarning, Pages = options.Pages };
        renderer.RenderDocument(document);

        MakeDeterministic(context.Document);

        using var stream = new System.IO.MemoryStream();
        context.Document.Generate(stream);
        return Normalize(stream.ToArray());
    }

    // A PDF's CreationDate/ModDate (stamped with DateTime.Now by Sparkle's Info constructor)
    // varies per save, so identical input produces different bytes. Pin it to a fixed value.
    // Unlike PdfSharp, Sparkle emits no trailer /ID and no XMP metadata, so there's nothing
    // else to pin at the document-metadata level.
    static readonly DateTime fixedTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static void MakeDeterministic(SparkleDoc document)
    {
        document.Info.Creator = "Morph";
        document.Info.Created = fixedTimestamp;
        document.Info.Modified = fixedTimestamp;
    }

    // Sparkle prefixes each embedded (composite/CID) font's PostScript name with a random
    // 6-uppercase-letter tag (e.g. "YZGTLG+Aptos") generated from a freshly seeded Random —
    // see Siberix.Sparkle.PDF.Font's constructor. There's no override hook, and it's the only
    // source of per-save variance once timestamps are pinned. Remap tags to deterministic ones
    // (AAAAAA, AAAAAB, … by first appearance). The tag appears after "/BaseFont /" and
    // "/FontName /" (Sparkle's Atoms.Name writer emits "<key> /<value>", not PdfSharp's
    // "<key>/<value>"). Every replacement is the same length as what it replaces, so the buffer
    // is patched in place.
    static readonly byte[] baseFontMarker = "/BaseFont /"u8.ToArray();
    static readonly byte[] fontNameMarker = "/FontName /"u8.ToArray();

    static byte[] Normalize(byte[] pdf)
    {
        var map = new Dictionary<string, string>();
        var index = 0;
        while (index < pdf.Length)
        {
            var marker = MatchesAt(pdf, index, baseFontMarker) ? baseFontMarker
                : MatchesAt(pdf, index, fontNameMarker) ? fontNameMarker
                : null;
            if (marker == null)
            {
                index++;
                continue;
            }

            // A match needs six uppercase letters and a '+' right after the marker; anything
            // else means this wasn't a subset tag and scanning continues from the next byte.
            var tagStart = index + marker.Length;
            if (tagStart + 7 > pdf.Length || pdf[tagStart + 6] != (byte) '+' || !IsUppercaseTag(pdf, tagStart))
            {
                index++;
                continue;
            }

            var original = Encoding.ASCII.GetString(pdf, tagStart, 6);
            if (!map.TryGetValue(original, out var replacement))
            {
                replacement = DeterministicTag(map.Count);
                map[original] = replacement;
            }

            for (var offset = 0; offset < 6; offset++)
            {
                pdf[tagStart + offset] = (byte) replacement[offset];
            }

            index = tagStart + 7;
        }

        return pdf;
    }

    static bool IsUppercaseTag(byte[] pdf, int start)
    {
        for (var offset = 0; offset < 6; offset++)
        {
            if (pdf[start + offset] is < (byte) 'A' or > (byte) 'Z')
            {
                return false;
            }
        }

        return true;
    }

    static bool MatchesAt(byte[] pdf, int index, byte[] pattern)
    {
        if (index + pattern.Length > pdf.Length)
        {
            return false;
        }

        for (var offset = 0; offset < pattern.Length; offset++)
        {
            if (pdf[index + offset] != pattern[offset])
            {
                return false;
            }
        }

        return true;
    }

    static string DeterministicTag(int index)
    {
        var tag = new char[6];
        for (var position = 5; position >= 0; position--)
        {
            tag[position] = (char) ('A' + index % 26);
            index /= 26;
        }

        return new(tag);
    }
}
