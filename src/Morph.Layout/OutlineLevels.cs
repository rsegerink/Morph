using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Morph.Layout;

// The w:outlineLvl of every w:p that has one below 9 (body text), keyed by the paragraph's Source ordinal:
// the paragraph's own, else its style's, else through the style's basedOn chain. A w:p without a
// w:pStyle, or naming one the styles part lacks, takes the default paragraph style. A styleId defined
// twice is its last definition, as in Word (DocumentParser.DropRedefinedStyles). DocumentParser keeps no
// outline level, so it is read from the package here.
static class OutlineLevels
{
    const int bodyText = 9;

    public static Dictionary<int, int> Of(WordprocessingDocument package)
    {
        var styles = new Dictionary<string, W.Style>(StringComparer.Ordinal);
        W.Style? defaultStyle = null;
        foreach (var style in package.MainDocumentPart?.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [])
        {
            if (style.Type?.Value != W.StyleValues.Paragraph ||
                style.StyleId?.Value is not { } styleId)
            {
                continue;
            }

            styles[styleId] = style;
            if (style.Default?.Value == true)
            {
                defaultStyle = style;
            }
        }

        var levels = new Dictionary<int, int>();
        foreach (var (paragraph, source) in SourceRuns.IndexAllParagraphs(SourceRuns.Stories(package)))
        {
            var properties = paragraph.ParagraphProperties;
            var style = properties?.ParagraphStyleId?.Val?.Value is { } styleId &&
                        styles.TryGetValue(styleId, out var named)
                ? named
                : defaultStyle;
            var level = properties?.OutlineLevel?.Val?.Value ?? StyleLevel(style, styles);
            if (level is >= 0 and < bodyText)
            {
                levels[source] = level.Value;
            }
        }

        return levels;
    }

    static int? StyleLevel(W.Style? style, Dictionary<string, W.Style> styles)
    {
        var seen = new HashSet<W.Style>(ReferenceEqualityComparer.Instance);
        while (style != null &&
               seen.Add(style))
        {
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } level)
            {
                return level;
            }

            style = style.BasedOn?.Val?.Value is { } basedOn
                ? styles.GetValueOrDefault(basedOn)
                : null;
        }

        return null;
    }
}
