using System.Numerics;

namespace Morph.Layout;

/// <summary>
/// Walks a laid-out tree and places every glyph where PdfPainter draws it: the face from the painter's
/// own context, one glyph per character at the face's advance plus tracking, kerned by the same shifts
/// (PdfPainter.Glyphs), stretched by w:w about the run start, inside the transforms of the groups that
/// hold the line. Glyphs then join into words, split at spaces, at gaps no character fills (a tab), and
/// where the baseline shifts.
/// </summary>
sealed class LayoutWalker(PdfRenderContext context)
{
    // Two glyphs further apart than this without a character between them are a tab stop, not one word.
    const double tabGapPoints = 1;

    readonly Dictionary<ParagraphElement, int> paragraphIndex = new(ReferenceEqualityComparer.Instance);
    readonly List<LayoutParagraph> paragraphs = [];

    public LayoutDocument Walk(LaidOutDocument document, PageRange? range)
    {
        var pages = new List<LayoutPage>();
        foreach (var page in document.Pages)
        {
            if (range is { } bounds &&
                (page.Number < bounds.Start || page.Number > bounds.End))
            {
                continue;
            }

            var lines = new List<LayoutLine>();
            foreach (var item in page.Items)
            {
                Visit(item, Matrix3x2.Identity, lines);
            }

            pages.Add(new(pages.Count + 1, page.Settings.WidthPoints, page.Settings.HeightPoints, lines));
        }

        return new(pages, paragraphs);
    }

    // The group transforms as PdfPainter.PaintScaledGroup / PaintRotatedGroup push them. Tables, header and
    // footer bands as such are not separated yet; table rows are skipped.
    void Visit(PlacedItem item, Matrix3x2 transform, List<LayoutLine> lines)
    {
        switch (item)
        {
            case PlacedLine line:
                if (Line(line, transform) is { } placed)
                {
                    lines.Add(placed);
                }

                break;
            case PlacedClipGroup clip:
                foreach (var child in clip.Items)
                {
                    Visit(child, transform, lines);
                }

                break;
            case PlacedScaledGroup scaled:
                var scale = Matrix3x2.CreateScale(scaled.Scale) * Matrix3x2.CreateTranslation(scaled.X, scaled.Y);
                foreach (var child in scaled.Items)
                {
                    Visit(child, scale * transform, lines);
                }

                break;
            case PlacedRotatedGroup rotated:
                var rotation = Matrix3x2.CreateRotation(
                    (float) (rotated.RotationDegrees * Math.PI / 180),
                    new(rotated.X + rotated.Width / 2, rotated.Y + rotated.Height / 2));
                foreach (var child in rotated.Items)
                {
                    Visit(child, rotation * transform, lines);
                }

                break;
        }
    }

    readonly record struct PlacedGlyph(char Character, double Left, double Right, double Top, double Bottom, double Baseline, PdfFace Face, RunProperties Properties, double SizePoints, bool IsListLabel);

    LayoutLine? Line(PlacedLine line, Matrix3x2 transform)
    {
        var glyphs = new List<PlacedGlyph>();
        var markerRun = line.LineIndex == 0 && line.Paragraph.Properties.Numbering is { Text.Length: > 0 } ? 0 : -1;
        for (var runIndex = 0; runIndex < line.Runs.Count; runIndex++)
        {
            var run = line.Runs[runIndex];
            if (run.Leader != TabLeader.None ||
                string.IsNullOrEmpty(run.Text))
            {
                continue;
            }

            AddGlyphs(glyphs, run, line.Baseline - run.BaselineShift, runIndex == markerRun);
        }

        var words = Words(glyphs, transform);
        if (words.Count == 0)
        {
            return null;
        }

        return new(Transform(transform, line.X, line.Y, line.X + line.Width, line.Y + line.Height),
            Transform(transform, line.X, line.Baseline, line.X, line.Baseline).Y,
            ParagraphIndex(line.Paragraph),
            line.LineIndex,
            words);
    }

    // PdfPainter.DrawTracked / Glyphs: the pen starts at the run's X, each glyph sits at the pen plus its
    // kern shift and moves the pen by its advance plus the tracking.
    void AddGlyphs(List<PlacedGlyph> glyphs, PlacedRun run, double baseline, bool isListLabel)
    {
        var properties = run.Properties;
        var face = context.GetFace(properties);
        var metrics = face.Metrics;
        var sizePoints = VerticalRunPosition.RenderSizePoints(properties);
        var kernShifts = CanonicalParagraphMeasurer.KerningEnabled(properties)
            ? CanonicalTextMeasurer.KernShiftsPoints(metrics, run.Text, sizePoints, context.FontWidthScale)
            : null;
        var characterScale = properties.CharacterScalePercent / 100;
        var top = baseline - (double) metrics.Ascender / metrics.UnitsPerEm * sizePoints;
        var bottom = baseline - (double) metrics.Descender / metrics.UnitsPerEm * sizePoints;
        var pen = 0d;
        var text = run.Text;
        var i = 0;
        while (i < text.Length)
        {
            var length = char.IsHighSurrogate(text[i]) &&
                         i + 1 < text.Length ? 2 : 1;
            var codepoint = char.ConvertToUtf32(text, i);
            var advance = (double) metrics.AdvanceUnits(codepoint) / metrics.UnitsPerEm * sizePoints;
            var left = pen + (kernShifts?[i] ?? 0);
            for (var unit = 0; unit < length; unit++)
            {
                glyphs.Add(new(
                    text[i + unit],
                    run.X + left * characterScale,
                    run.X + (left + advance) * characterScale,
                    top,
                    bottom,
                    baseline,
                    face,
                    properties,
                    sizePoints,
                    isListLabel));
            }

            pen += advance + properties.CharacterSpacingPoints;
            i += length;
        }
    }

    static List<LayoutWord> Words(List<PlacedGlyph> glyphs, Matrix3x2 transform)
    {
        var words = new List<LayoutWord>();
        var start = -1;
        for (var index = 0; index <= glyphs.Count; index++)
        {
            var ends = index == glyphs.Count ||
                       char.IsWhiteSpace(glyphs[index].Character) ||
                       (start >= 0 &&
                        (glyphs[index].IsListLabel != glyphs[index - 1].IsListLabel ||
                         // a superscript note reference or subscript is a word of its own
                         glyphs[index].Baseline != glyphs[index - 1].Baseline ||
                         glyphs[index].Left - glyphs[index - 1].Right > tabGapPoints));
            if (ends && start >= 0)
            {
                words.Add(Word(glyphs, start, index, transform));
                start = -1;
            }

            if (index < glyphs.Count &&
                start < 0 &&
                !char.IsWhiteSpace(glyphs[index].Character))
            {
                start = index;
            }
        }

        return words;
    }

    static LayoutWord Word(List<PlacedGlyph> glyphs, int start, int end, Matrix3x2 transform)
    {
        var text = new char[end - start];
        double left = double.MaxValue, right = double.MinValue, top = double.MaxValue, bottom = double.MinValue;
        for (var index = start; index < end; index++)
        {
            var glyph = glyphs[index];
            text[index - start] = glyph.Character;
            left = Math.Min(left, glyph.Left);
            right = Math.Max(right, glyph.Right);
            top = Math.Min(top, glyph.Top);
            bottom = Math.Max(bottom, glyph.Bottom);
        }

        var first = glyphs[start];
        return new(
            new(text),
            Transform(transform, left, top, right, bottom),
            Transform(transform, left, first.Baseline, left, first.Baseline).Y,
            first.Face.Family ?? first.Properties.FontFamily,
            first.SizePoints,
            first.Properties.Bold,
            first.Properties.Italic,
            first.IsListLabel);
    }

    // The axis-aligned box around a rectangle's four corners after the transform.
    static BoundingBox Transform(Matrix3x2 transform, double left, double top, double right, double bottom)
    {
        if (transform.IsIdentity)
        {
            return new(left, top, right - left, bottom - top);
        }

        Span<Vector2> corners =
        [
            Vector2.Transform(new((float) left, (float) top), transform),
            Vector2.Transform(new((float) right, (float) top), transform),
            Vector2.Transform(new((float) left, (float) bottom), transform),
            Vector2.Transform(new((float) right, (float) bottom), transform)
        ];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var corner in corners)
        {
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }

        return new(minX, minY, maxX - minX, maxY - minY);
    }

    int ParagraphIndex(ParagraphElement paragraph)
    {
        if (!paragraphIndex.TryGetValue(paragraph, out var index))
        {
            index = paragraphs.Count;
            paragraphIndex[paragraph] = index;
            var properties = paragraph.Properties;
            paragraphs.Add(new(
                index,
                properties.StyleId ?? properties.ResolvedStyleId,
                properties.Numbering is { Text.Length: > 0 } numbering ? numbering.Text : null,
                properties.Numbering?.Level,
                paragraph.Source));
        }

        return index;
    }
}
