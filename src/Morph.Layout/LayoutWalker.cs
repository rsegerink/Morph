using System.Numerics;
using System.Text;

namespace Morph.Layout;

/// <summary>
/// Walks a laid-out tree and places every glyph where PdfPainter draws it: the face from the painter's
/// own context, one glyph per character at the face's advance plus tracking, kerned by the same shifts
/// (PdfPainter.Glyphs), stretched by w:w about the run start, inside the transforms of the groups that
/// hold the line. Glyphs then join into words, split at spaces, at gaps no character fills (a tab), and
/// where the baseline shifts.
/// </summary>
sealed class LayoutWalker(PdfRenderContext context, ParsedDocument document, Dictionary<int, int> outlineLevels)
{
    // Two glyphs further apart than this without a character between them are a tab stop, not one word.
    const double tabGapPoints = 1;

    const char softHyphen = '\u00AD';

    readonly Dictionary<ParagraphElement, int> paragraphIndex = new(ReferenceEqualityComparer.Instance);
    readonly List<LayoutParagraph> paragraphs = [];
    readonly Dictionary<TableElement, int> tableIndex = new(ReferenceEqualityComparer.Instance);
    readonly List<LayoutTable> tables = [];

    // Each table's own cell paragraphs (not those of tables nested in its cells) to the cell holding them.
    readonly Dictionary<ParagraphElement, LayoutCellRef> cellOf = new(ReferenceEqualityComparer.Instance);

    // Every parsed paragraph, cell and text box paragraphs included, to the story it belongs to. Fragmenter
    // lays some out as copies: a paragraph holding a page field once per page (SubstitutePageFields), with the
    // original's paragraph and run properties, which pageFieldOriginals finds it back through; and a paragraph
    // on a mirrored page (MirrorForPage) with the original's run list, which runsOwner finds it back through.
    readonly Dictionary<ParagraphElement, LayoutStory> storyOf = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ParagraphProperties, List<ParagraphElement>> pageFieldOriginals = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<IReadOnlyList<Run>, ParagraphElement> runsOwner = new(ReferenceEqualityComparer.Instance);

    // Unwarped WordArt (a text box Morph reads as WordArt, like business/06's "LOGO") is drawn through a
    // paragraph Fragmenter.LayoutWordArtText builds at layout time, one centred run of the WordArt's text
    // and font; nothing else links it back, so it is found by those.
    readonly Dictionary<(string Text, string FontFamily, bool Bold, bool Italic, string? ColorHex), LayoutStory> wordArtOf = [];
    readonly HashSet<HeaderFooterContent> bandsSeen = new(ReferenceEqualityComparer.Instance);

    // Every footnote and endnote paragraph to its note's w:id. Fragmenter places them as parsed.
    readonly Dictionary<ParagraphElement, string> noteOf = new(ReferenceEqualityComparer.Instance);

    // Every paragraph inside a text box (w:txbxContent), in any story. Fragmenter places a box's content
    // as parsed (PlaceTextBox).
    readonly HashSet<ParagraphElement> inTextBox = new(ReferenceEqualityComparer.Instance);

    // Every note reference run's properties to the note it cites. DocumentParser gives each reference mark
    // its own copy of the properties, which the measurer draws with unchanged unless it emboldens them.
    readonly Dictionary<RunProperties, (string? Footnote, string? Endnote)> noteReferenceOf = new(ReferenceEqualityComparer.Instance);

    // What the items being visited sit inside: the group transforms above them, and the table cell.
    readonly record struct Scope(Matrix3x2 Transform, int? Table, LayoutCellRef? Cell, bool IsRepeatedHeader);

    public LayoutDocument Walk(LaidOutDocument laidOut, PageRange? range)
    {
        AddStory(document.Elements, LayoutStory.Body, null);
        AddBands(document.Header, document.Footer, document.FirstPageHeader, document.FirstPageFooter, document.EvenPageHeader, document.EvenPageFooter);
        foreach (var footnote in document.Footnotes)
        {
            AddStory(footnote.Elements, LayoutStory.Footnote, footnote.Id);
        }

        foreach (var endnote in document.Endnotes)
        {
            AddStory(endnote.Elements, LayoutStory.Endnote, endnote.Id);
        }

        foreach (var page in laidOut.Pages)
        {
            if (page.Settings.Bands is { } bands)
            {
                AddBands(bands.Header, bands.Footer, bands.FirstPageHeader, bands.FirstPageFooter, bands.EvenPageHeader, bands.EvenPageFooter);
            }
        }

        var pages = new List<LayoutPage>();
        foreach (var page in laidOut.Pages)
        {
            if (range is { } bounds &&
                (page.Number < bounds.Start || page.Number > bounds.End))
            {
                continue;
            }

            var lines = new List<LayoutLine>();
            foreach (var item in page.Items)
            {
                Visit(item, new(Matrix3x2.Identity, null, null, false), lines);
            }

            pages.Add(new(pages.Count + 1, page.Settings.WidthPoints, page.Settings.HeightPoints, lines));
        }

        return new(pages, paragraphs, tables);
    }

    void AddBands(params HeaderFooterContent?[] bands)
    {
        for (var index = 0; index < bands.Length; index++)
        {
            if (bands[index] is { } band &&
                bandsSeen.Add(band))
            {
                // header, footer, first-page header, first-page footer, ...
                AddStory(band.Elements, index % 2 == 0 ? LayoutStory.Header : LayoutStory.Footer, null);
            }
        }
    }

    void AddStory(IReadOnlyList<DocumentElement> elements, LayoutStory story, string? noteId, bool textBox = false)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case ParagraphElement paragraph:
                    AddParagraph(paragraph, story, noteId, textBox);
                    break;
                // A content control is laid out through its one synthetic paragraph.
                case ContentControlElement {CellParagraph: { } paragraph}:
                    AddParagraph(paragraph, story, noteId, textBox);
                    break;
                case TableElement table:
                    foreach (var cell in table.Rows.SelectMany(_ => _.Cells))
                    {
                        AddStory(cell.Content, story, noteId, textBox);
                        AddStory(cell.Floats, story, noteId, textBox);
                    }

                    break;
                case FloatingTextBoxElement box:
                    AddStory(box.Content, story, noteId, true);
                    break;
                case PositionedFrameElement frame:
                    AddStory(frame.Content, story, noteId, textBox);
                    break;
                case WordArtElement wordArt:
                    wordArtOf.TryAdd((wordArt.Text, wordArt.FontFamily, wordArt.Bold, wordArt.Italic, wordArt.FillColorHex), story);
                    break;
                case FloatingWordArtElement wordArt:
                    wordArtOf.TryAdd((wordArt.Text, wordArt.FontFamily, wordArt.Bold, wordArt.Italic, wordArt.FillColorHex), story);
                    break;
            }
        }
    }

    void AddParagraph(ParagraphElement paragraph, LayoutStory story, string? noteId, bool textBox)
    {
        storyOf.TryAdd(paragraph, story);
        if (paragraph.Runs.Count > 0)
        {
            runsOwner.TryAdd(paragraph.Runs, paragraph);
        }

        if (textBox)
        {
            inTextBox.Add(paragraph);
        }

        if (noteId != null)
        {
            noteOf.TryAdd(paragraph, noteId);
        }

        foreach (var run in paragraph.Runs)
        {
            if (run.FootnoteReferenceId != null ||
                run.EndnoteReferenceId != null)
            {
                noteReferenceOf.TryAdd(run.Properties, (run.FootnoteReferenceId, run.EndnoteReferenceId));
            }
        }

        if (paragraph.Runs.Any(_ => _.PageField != PageFieldKind.None))
        {
            if (!pageFieldOriginals.TryGetValue(paragraph.Properties, out var originals))
            {
                pageFieldOriginals[paragraph.Properties] = originals = [];
            }

            originals.Add(paragraph);
        }
    }

    // The parsed paragraph a placed line belongs to: itself, or for a copy the original it was made from.
    ParagraphElement Original(ParagraphElement paragraph)
    {
        if (storyOf.ContainsKey(paragraph))
        {
            return paragraph;
        }

        if (paragraph.Runs.Count > 0 &&
            runsOwner.TryGetValue(paragraph.Runs, out var owner))
        {
            return owner;
        }

        if (!pageFieldOriginals.TryGetValue(paragraph.Properties, out var originals))
        {
            return paragraph;
        }

        foreach (var original in originals)
        {
            if (original.Runs.Count == paragraph.Runs.Count &&
                original.Runs.Zip(paragraph.Runs).All(_ => ReferenceEquals(_.First.Properties, _.Second.Properties)))
            {
                return original;
            }
        }

        return paragraph;
    }

    // The group transforms as PdfPainter.PaintScaledGroup / PaintRotatedGroup push them, and table cells in
    // the order PdfPainter.PaintTableRow draws them (floats, then content).
    void Visit(PlacedItem item, Scope scope, List<LayoutLine> lines)
    {
        switch (item)
        {
            case PlacedLine line:
                if (Line(line, scope) is { } placed)
                {
                    lines.Add(placed);
                }

                break;
            case PlacedTableRow row:
                var table = TableIndex(row.Table, scope.Table);
                foreach (var cell in row.Cells)
                {
                    var cellScope = scope with
                    {
                        Table = table,
                        Cell = CellOf(cell),
                        IsRepeatedHeader = scope.IsRepeatedHeader || row.IsRepeatedHeader
                    };
                    foreach (var child in cell.Floats.Concat(cell.Content))
                    {
                        Visit(child, cellScope, lines);
                    }
                }

                break;
            case PlacedClipGroup clip:
                foreach (var child in clip.Items)
                {
                    Visit(child, scope, lines);
                }

                break;
            case PlacedScaledGroup scaled:
                var scale = Matrix3x2.CreateScale(scaled.Scale) * Matrix3x2.CreateTranslation(scaled.X, scaled.Y);
                foreach (var child in scaled.Items)
                {
                    Visit(child, scope with { Transform = scale * scope.Transform }, lines);
                }

                break;
            case PlacedRotatedGroup rotated:
                var rotation = Matrix3x2.CreateRotation(
                    (float) (rotated.RotationDegrees * Math.PI / 180),
                    new(rotated.X + rotated.Width / 2, rotated.Y + rotated.Height / 2));
                foreach (var child in rotated.Items)
                {
                    Visit(child, scope with { Transform = rotation * scope.Transform }, lines);
                }

                break;
        }
    }

    // A placed row leaves out the cells a vertical merge continues (the restart cell's box covers them), so
    // the n-th placed cell need not be the n-th w:tc. The cell is found through its paragraphs instead,
    // which are the parsed ones. A cell with no paragraph of its own (only a nested table) has no ref.
    LayoutCellRef? CellOf(PlacedCell cell)
    {
        foreach (var item in cell.Content)
        {
            if (item is PlacedLine line &&
                (cellOf.TryGetValue(line.Paragraph, out var found) ||
                 cellOf.TryGetValue(Original(line.Paragraph), out found)))
            {
                return found;
            }
        }

        return null;
    }

    int TableIndex(TableElement table, int? parent)
    {
        if (tableIndex.TryGetValue(table, out var index))
        {
            return index;
        }

        index = tables.Count;
        tableIndex[table] = index;
        var columnCount = 0;
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var column = row.GridBefore;
            for (var cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
            {
                var cell = row.Cells[cellIndex];
                var reference = new LayoutCellRef(index, rowIndex, column, cell.Properties.GridSpan, RowSpan(table, rowIndex, column));
                foreach (var element in cell.Content)
                {
                    if ((element as ParagraphElement ?? (element as ContentControlElement)?.CellParagraph) is { } paragraph)
                    {
                        cellOf.TryAdd(paragraph, reference);
                    }
                }

                column += cell.Properties.GridSpan;
            }

            columnCount = Math.Max(columnCount, column + row.GridAfter);
        }

        tables.Add(new(index, parent, table.Rows.Count, columnCount));
        return index;
    }

    // The rows a w:vMerge restart at this grid column covers: itself plus each following row whose cell at
    // the same column continues it.
    static int RowSpan(TableElement table, int rowIndex, int column)
    {
        if (CellAt(table.Rows[rowIndex], column) is not { Properties.VerticalMerge: VerticalMergeType.Restart })
        {
            return 1;
        }

        var span = 1;
        while (rowIndex + span < table.Rows.Count &&
               CellAt(table.Rows[rowIndex + span], column) is { Properties.VerticalMerge: VerticalMergeType.Continue })
        {
            span++;
        }

        return span;
    }

    static TableCell? CellAt(TableRow row, int column)
    {
        var start = row.GridBefore;
        foreach (var cell in row.Cells)
        {
            if (start == column)
            {
                return cell;
            }

            start += cell.Properties.GridSpan;
        }

        return null;
    }

    // Right is where the glyph's advance ends; PenRight adds the tracking, where the next glyph of the
    // same word starts.
    readonly record struct PlacedGlyph(char Character, double Left, double Right, double PenRight, double Top, double Bottom, double Baseline, PdfFace Face, RunProperties Properties, double SizePoints, bool IsListLabel);

    LayoutLine? Line(PlacedLine line, Scope scope)
    {
        var transform = scope.Transform;
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
            ParagraphIndex(line.Paragraph, scope.Cell),
            line.LineIndex,
            words,
            scope.IsRepeatedHeader);
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
                    run.X + (left + advance + properties.CharacterSpacingPoints) * characterScale,
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

    List<LayoutWord> Words(List<PlacedGlyph> glyphs, Matrix3x2 transform)
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
                         glyphs[index].Left - glyphs[index - 1].PenRight > tabGapPoints));
            if (ends && start >= 0)
            {
                if (Word(glyphs, start, index, transform) is { } word)
                {
                    words.Add(word);
                }

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

    // A soft hyphen (U+00AD) only marks where Word may break the word, so it is left out of the text, or
    // "Wien\u00ADdels" would not match "Wiendels" downstream; the box still covers whatever Morph drew for it.
    // A word of nothing but soft hyphens is no word.
    LayoutWord? Word(List<PlacedGlyph> glyphs, int start, int end, Matrix3x2 transform)
    {
        var text = new StringBuilder(end - start);
        double left = double.MaxValue, right = double.MinValue, top = double.MaxValue, bottom = double.MinValue;
        (string? Footnote, string? Endnote) reference = default;
        for (var index = start; index < end; index++)
        {
            var glyph = glyphs[index];
            if (reference == default)
            {
                reference = noteReferenceOf.GetValueOrDefault(glyph.Properties);
            }

            if (glyph.Character != softHyphen)
            {
                text.Append(glyph.Character);
            }

            left = Math.Min(left, glyph.Left);
            right = Math.Max(right, glyph.Right);
            top = Math.Min(top, glyph.Top);
            bottom = Math.Max(bottom, glyph.Bottom);
        }

        if (text.Length == 0)
        {
            return null;
        }

        var first = glyphs[start];
        return new(
            text.ToString(),
            Transform(transform, left, top, right, bottom),
            Transform(transform, left, first.Baseline, left, first.Baseline).Y,
            first.Face.Family ?? first.Properties.FontFamily,
            first.SizePoints,
            first.Properties.Bold,
            first.Properties.Italic,
            first.IsListLabel,
            Rotation(transform),
            reference.Footnote,
            reference.Endnote);
    }

    // The baseline's direction after the transform, in degrees clockwise (Y runs down the page).
    static double Rotation(Matrix3x2 transform)
    {
        if (transform.IsIdentity)
        {
            return 0;
        }

        var degrees = Math.Round(Math.Atan2(transform.M12, transform.M11) * 180 / Math.PI, 4);
        return degrees == 0 ? 0 : degrees;
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

    int ParagraphIndex(ParagraphElement paragraph, LayoutCellRef? cell)
    {
        paragraph = Original(paragraph);
        if (!paragraphIndex.TryGetValue(paragraph, out var index))
        {
            index = paragraphs.Count;
            paragraphIndex[paragraph] = index;
            var properties = paragraph.Properties;
            var (story, textBox) = storyOf.TryGetValue(paragraph, out var parsed)
                ? (parsed, inTextBox.Contains(paragraph))
                : WordArtStory(paragraph) is { } wordArt
                    ? (wordArt, true)
                    : (LayoutStory.Body, false);
            paragraphs.Add(new(
                index,
                properties.StyleId ?? properties.ResolvedStyleId,
                properties.Numbering is { Text.Length: > 0 } numbering ? numbering.Text : null,
                properties.Numbering?.Level,
                paragraph.Source,
                cellOf.GetValueOrDefault(paragraph) ?? cell,
                story,
                noteOf.GetValueOrDefault(paragraph),
                textBox,
                paragraph.Source is { } source && outlineLevels.TryGetValue(source, out var level) ? level : null));
        }

        return index;
    }

    LayoutStory? WordArtStory(ParagraphElement paragraph) =>
        paragraph is {Runs: [{Properties: var run} single], Properties.Alignment: TextAlignment.Center} &&
        wordArtOf.TryGetValue((single.Text, run.FontFamily, run.Bold, run.Italic, run.ColorHex), out var story)
            ? story
            : null;
}
