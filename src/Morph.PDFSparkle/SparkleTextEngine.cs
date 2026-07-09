namespace Morph;

/// <summary>
/// Paragraph layout and drawing for the Sparkle backend. Measures text via
/// IGraphics.StringWidth() and draws with IGraphics.DrawString(). Mirrors the
/// PdfTextEngine logic but uses Sparkle primitives instead of PdfSharp.
/// </summary>
sealed class SparkleTextEngine(SparkleRenderContext context) : IParagraphMeasurer
{
    // A throw-away page used purely for StringWidth() measurement (no output).
    readonly IPage measurePage = new SparkleDoc().AddPage(2000, 2000);

    // Space width, ascent and raw line height are constant per SparkleFont; the layout loop used
    // to re-measure a space per whitespace token and recompute ascent/height per run. Mirrors
    // PdfTextEngine.Metrics.
    readonly Dictionary<SparkleFont, (float SpaceWidth, float Ascent, float RawHeight)> fontMetricsCache = [];

    (float SpaceWidth, float Ascent, float RawHeight) Metrics(SparkleFont font)
    {
        if (!fontMetricsCache.TryGetValue(font, out var metrics))
        {
            SetMeasureFont(font);
            var fontMetrics = measurePage.Graphics.FontMetrics;
            metrics = (measurePage.Graphics.StringWidth(" "), fontMetrics.Ascent, fontMetrics.Height);
            fontMetricsCache[font] = metrics;
        }

        return metrics;
    }

    // ---- IParagraphMeasurer ----

    // The table-cell measurers take the cell's inner width and remove BOTH indents to get the wrap
    // width, exactly as RenderInBounds draws it and as the Skia/ImageSharp measurers do. Previously
    // they passed the raw width to Layout, so a left- or right-indented cell paragraph was measured
    // wider than it renders — the row was under-sized.
    public List<float> LayoutParagraphForMeasurement(ParagraphElement paragraph, float maxWidth)
    {
        var lines = Layout(paragraph, maxWidth - (float)Indent(paragraph) - (float)RightIndent(paragraph));
        var heights = new List<float>(lines.Count);
        foreach (var line in lines)
            heights.Add(line.Height);
        if (heights.Count == 0)
            heights.Add(EmptyLineHeight(paragraph));
        return heights;
    }

    // Autofit column widths use this for a cell paragraph's natural (unwrapped) and minimum (widest
    // word) content width, probed with sentinel widths. Returns the bare widest line width — the
    // shared TableLayout adds cell padding/margin itself. Adding the left indent here made the PDF's
    // autofit columns a left-indent wider than the Skia/ImageSharp measurers (which return bare
    // widest); the raster is the reference, so match it.
    public float MeasureParagraphNaturalWidth(ParagraphElement paragraph, float maxWidth)
    {
        var widest = 0f;
        foreach (var line in Layout(paragraph, maxWidth))
            widest = Math.Max(widest, line.Width);
        return widest;
    }

    public float MeasureParagraphHeightWithWidth(ParagraphElement paragraph, float maxWidth) =>
        MeasureHeight(paragraph, maxWidth - (float)Indent(paragraph) - (float)RightIndent(paragraph));

    // Height the paragraph will consume in the page flow: MeasureHeight with the cross-paragraph
    // spacing collapse that Draw applies (max(after, before) between neighbours) folded in, so
    // keep decisions test the height that drawing will actually use. previousSpacingAfter is the
    // spacing-after of whatever renders before this paragraph. maxWidth is the full section
    // content width; the paragraph's left and right indents are subtracted here so the measured
    // wrap matches what Draw draws (both now honour the right indent — issue #151 follow-up).
    public float MeasureFlowHeight(ParagraphElement paragraph, float maxWidth, float previousSpacingAfter) =>
        MeasureHeight(paragraph, maxWidth - (float)Indent(paragraph) - (float)RightIndent(paragraph)) - Math.Min(SpacingBefore(paragraph), previousSpacingAfter);

    float MeasureHeight(ParagraphElement paragraph, float maxWidth)
    {
        var lines = Layout(paragraph, maxWidth);
        var total = SpacingBefore(paragraph) + SpacingAfter(paragraph);
        if (lines.Count == 0)
            return total + EmptyLineHeight(paragraph);
        foreach (var line in lines)
            total += line.Height;
        return total;
    }

    float EmptyLineHeight(ParagraphElement paragraph)
    {
        // Word collapses the end-of-cell mark after a nested table to zero height.
        if (paragraph.IsCollapsedCellMark || paragraph.IsAnchorOnlyMark)
            return 0;

        var properties = paragraph.Properties;
        var font = properties.ParagraphMarkRunProperties is { } markProps
            ? context.GetFont(markProps)
            : context.GetFont(DefaultFontSettings.DefaultFont, false, false, properties.ParagraphMarkFontSizePoints ?? 11);
        var rawHeight = Metrics(font).RawHeight;
        return properties.LineSpacingRule switch
        {
            LineSpacingRule.Exactly => (float)properties.LineSpacingPoints,
            LineSpacingRule.AtLeast => Math.Max(rawHeight, (float)properties.LineSpacingPoints),
            _ => rawHeight * (float)properties.LineSpacingMultiplier
        };
    }

    // ---- Spacing (mirrors PdfTextEngine) ----

    float SpacingBefore(ParagraphElement paragraph)
    {
        var props = paragraph.Properties;
        var sameStyle = props.StyleId != null && props.StyleId == context.LastParagraphStyleId;
        var collapse = props.ContextualSpacing && context.LastParagraphHadContextualSpacing && sameStyle;
        return collapse ? 0f : (float)props.SpacingBeforePoints;
    }

    internal static float SpacingAfter(ParagraphElement paragraph) =>
        paragraph.Properties.ContextualSpacing ? 0f : (float)paragraph.Properties.SpacingAfterPoints;

    void TrackContextualSpacing(ParagraphElement paragraph)
    {
        context.LastParagraphStyleId = paragraph.Properties.StyleId;
        context.LastParagraphHadContextualSpacing = paragraph.Properties.ContextualSpacing;
    }

    // ---- Drawing ----

    public Action? RequestNewPage { get; set; }

    public void Render(ParagraphElement paragraph)
    {
        var maxWidth = context.ContentWidth - (float)Indent(paragraph) - (float)RightIndent(paragraph);
        Draw(paragraph, context.ContentLeft + (float)Indent(paragraph), maxWidth, allowPageBreak: true);
    }

    // Draws the paragraph constrained to a bounded region (table cell), no page breaks.
    // maxWidth is the region's inner width; both indents come off the wrap width (a bulleted /
    // right-indented cell paragraph wraps within the indented region) while only the left indent
    // shifts the start position.
    public void RenderInBounds(ParagraphElement paragraph, float x, float maxWidth)
    {
        var indent = (float)Indent(paragraph);
        Draw(paragraph, x + indent, maxWidth - indent - (float)RightIndent(paragraph), allowPageBreak: false);
    }

    void Draw(ParagraphElement paragraph, float left, float availableWidth, bool allowPageBreak)
    {
        var lines = Layout(paragraph, availableWidth);

        // Word collapses adjacent paragraph spacing to max(after, before) — so in the page flow
        // only the excess of this paragraph's spacing-before over the previous paragraph's
        // spacing-after consumes height. Bounded (table-cell) rendering keeps the raw value: cell
        // measurement runs repeatedly and must stay independent of flow state. Mirrors PdfTextEngine.
        var spacingBefore = SpacingBefore(paragraph);
        if (allowPageBreak)
        {
            spacingBefore = Math.Max(0, spacingBefore - context.LastParagraphSpacingAfterPoints);

            // Word drops spacing-before at the top of an automatically broken page (one-shot,
            // set by the page renderer).
            if (context.SuppressPageTopSpacingBefore)
                spacingBefore = 0;
        }

        context.SuppressPageTopSpacingBefore = false;
        context.CurrentY += spacingBefore;

        if (lines.Count == 0)
        {
            context.CurrentY += EmptyLineHeight(paragraph);
            context.CurrentY += SpacingAfter(paragraph);
            TrackContextualSpacing(paragraph);
            if (allowPageBreak)
                context.LastParagraphSpacingAfterPoints = SpacingAfter(paragraph);
            return;
        }

        var alignment = paragraph.Properties.Alignment;
        var markerDrawn = false;

        // Word's widow/orphan control (w:widowControl, on by default — mapped to two lines on
        // each side of a split): a paragraph may not break leaving fewer than two lines at the
        // page bottom or carrying fewer than two lines forward. Abandoned at the top of a
        // page/column (moving cannot gain space). Mirrors PdfTextEngine.
        var widowControlled = allowPageBreak && paragraph.Properties.WidowControl && lines.Count >= 2;
        var forcedBreakIndex = -1;
        if (widowControlled && RequestNewPage != null)
        {
            var fit = CountLinesThatFit(lines, 0);
            if (fit < lines.Count)
            {
                var carried = lines.Count - fit;
                var moveWhole = fit < 2 || (carried == 1 && fit - 1 < 2);
                if (moveWhole && context.CurrentY > context.ContentTop)
                {
                    RequestNewPage();
                    left = context.ContentLeft + (float)Indent(paragraph);
                }

                forcedBreakIndex = PlanWidowBreak(lines, 0);
            }
        }

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (allowPageBreak &&
                context.CurrentY > context.ContentTop &&
                (context.CurrentY + line.Height > context.ContentBottom || lineIndex == forcedBreakIndex) &&
                RequestNewPage != null)
            {
                RequestNewPage();
                // RequestNewPage may advance to the next column instead of a new page; either way
                // the section's content-left can shift, so rebase this line's start onto the
                // current column.
                left = context.ContentLeft + (float)Indent(paragraph);
                if (widowControlled)
                    forcedBreakIndex = PlanWidowBreak(lines, lineIndex);
            }

            var g = context.Graphics;
            var lineTop = context.CurrentY;
            var baseline = lineTop + line.Ascent;

            // The first line's start shifts by its signed offset (see FirstLineOffset): right for a
            // first-line indent, LEFT (outdent) for a markerless hanging indent; its alignment box
            // resizes to match (Layout adjusted the wrap width the same way). Continuation lines sit
            // at the left indent unchanged.
            var firstLineOffset = lineIndex == 0 ? (float)FirstLineOffset(paragraph) : 0f;
            var lineWidth = availableWidth - firstLineOffset;
            var penX = left + firstLineOffset;
            var extraSpace = 0f;
            if (alignment == TextAlignment.Center)
                penX += Math.Max(0, (lineWidth - line.Width) / 2);
            else if (alignment == TextAlignment.Right)
                penX += Math.Max(0, lineWidth - line.Width);
            else if (alignment == TextAlignment.Justify && line is { IsLast: false, SpaceCount: > 0 })
                extraSpace = Math.Max(0, lineWidth - line.Width) / line.SpaceCount;

            if (!markerDrawn && paragraph.Properties.Numbering is { Text.Length: > 0 } numbering)
            {
                markerDrawn = true;
                if (g != null)
                {
                    var firstProps = paragraph.Runs.Count > 0 ? paragraph.Runs[0].Properties : new();
                    var markerFont = context.GetFont(firstProps.FontFamily, firstProps.Bold, false, firstProps.FontSizePoints);
                    SetFont(g, markerFont, firstProps);
                    var markerText = numbering.Text;
                    var markerWidth = g.StringWidth(markerText);
                    var hangingIndent = (float)paragraph.Properties.HangingIndentPoints;
                    var markerX = hangingIndent > 0.01f ? penX - hangingIndent : penX - markerWidth - 3;
                    g.DrawString(markerX, baseline - line.Ascent, markerText);
                }
            }

            foreach (var item in line.Items)
            {
                if (item.IsImage)
                {
                    DrawInlineImage(g, item, penX, baseline);
                    penX += item.Width;
                    continue;
                }

                if (item.IsTabFiller)
                {
                    if (g != null)
                        DrawTabLeader(g, item, penX, baseline);
                    penX += item.Width;
                    continue;
                }

                if (g != null && !string.IsNullOrEmpty(item.Text))
                    DrawItem(g, item, penX, baseline);

                penX += item.Width;
                if (item.IsSpace)
                    penX += extraSpace;
            }

            context.CurrentY += line.Height;
        }

        context.CurrentY += SpacingAfter(paragraph);
        TrackContextualSpacing(paragraph);
        if (allowPageBreak)
            context.LastParagraphSpacingAfterPoints = SpacingAfter(paragraph);
    }

    // Consecutive lines from startIndex that fit above the page bottom, measured from the
    // current flow position with the same float accumulation and comparison as the draw loop.
    // Mirrors PdfTextEngine.
    int CountLinesThatFit(List<Line> lines, int startIndex)
    {
        var y = context.CurrentY;
        var count = 0;
        for (var index = startIndex; index < lines.Count; index++)
        {
            if (y + lines[index].Height > context.ContentBottom)
                break;
            y += lines[index].Height;
            count++;
        }
        return count;
    }

    // Index of the line to force onto the next page so the split carries at least two lines
    // forward while leaving at least two behind, or -1 when the natural break already satisfies
    // the widow rule (or no earlier break can produce a valid split). Mirrors PdfTextEngine.
    int PlanWidowBreak(List<Line> lines, int startIndex)
    {
        var fit = CountLinesThatFit(lines, startIndex);
        var remaining = lines.Count - startIndex;
        if (fit >= remaining)
            return -1;

        if (remaining - fit == 1 && fit - 1 >= 2)
            return startIndex + fit - 1;

        return -1;
    }

    void DrawItem(IGraphics g, LineItem item, float penX, float baseline)
    {
        var props = item.Props;
        var drawBaseline = baseline;
        if (props.VerticalAlignment == VerticalRunAlignment.Superscript)
            drawBaseline -= (float)props.FontSizePoints * 0.33f;
        else if (props.VerticalAlignment == VerticalRunAlignment.Subscript)
            drawBaseline += (float)props.FontSizePoints * 0.14f;

        SetFont(g, item.Font, props);
        g.DrawString(penX, drawBaseline - item.Ascent, item.Text!);

        if (props.Underline)
        {
            g.Pen = context.GetPen(SparkleRenderContext.ParseColor(props.ColorHex), Math.Max(0.5f, item.Font.Size / 16f));
            var y = drawBaseline + item.Font.Size * 0.12f;
            g.DrawLine(penX, y, penX + item.Width, y);
        }

        if (props.Strikethrough)
        {
            g.Pen = context.GetPen(SparkleRenderContext.ParseColor(props.ColorHex), Math.Max(0.5f, item.Font.Size / 16f));
            var y = drawBaseline - item.Ascent * 0.3f;
            g.DrawLine(penX, y, penX + item.Width, y);
        }
    }

    void DrawTabLeader(IGraphics g, LineItem item, float penX, float baseline)
    {
        if (item.Width <= 0 || item.TabLeader == TabLeader.None)
            return;

        var color = SparkleRenderContext.ParseColor(item.Props.ColorHex);

        if (item.TabLeader == TabLeader.Underscore)
        {
            g.Pen = context.GetPen(color, Math.Max(0.5f, item.Font.Size / 16f));
            var y = baseline + item.Font.Size * 0.12f;
            g.DrawLine(penX, y, penX + item.Width, y);
            return;
        }

        var leaderChar = item.TabLeader switch
        {
            TabLeader.Dot       => '.',
            TabLeader.Hyphen    => '-',
            TabLeader.MiddleDot => '·',
            TabLeader.Heavy     => '—',
            _                   => '.'
        };

        var glyphWidth = LeaderGlyphWidth(leaderChar, item.Font);
        if (glyphWidth <= 0) return;

        var count = (int)Math.Floor((item.Width - glyphWidth) / glyphWidth);
        if (count <= 0) return;

        SetFont(g, item.Font, item.Props);
        g.DrawString(penX, baseline - item.Ascent, new string(leaderChar, count));
    }

    // A TOC redraws the same dot leader on every entry; the glyph width is constant per font.
    // Mirrors PdfTextEngine.
    readonly Dictionary<(char Leader, SparkleFont Font), float> leaderGlyphWidths = [];

    float LeaderGlyphWidth(char leaderChar, SparkleFont font)
    {
        var key = (leaderChar, font);
        if (!leaderGlyphWidths.TryGetValue(key, out var width))
        {
            SetMeasureFont(font);
            width = measurePage.Graphics.StringWidth(leaderChar.ToString());
            leaderGlyphWidths[key] = width;
        }

        return width;
    }

    void DrawInlineImage(IGraphics? g, LineItem item, float penX, float baseline)
    {
        if (g == null || item.ImageData == null) return;
        try
        {
            var img = context.GetImage(item.ImageData);
            g.DrawImage(img, penX, baseline - item.ImageHeight, item.ImageWidth, item.ImageHeight);
        }
        catch { }
    }

    void SetFont(IGraphics g, SparkleFont font, RunProperties props)
    {
        g.Font = font;
        g.Brush = context.GetBrush(SparkleRenderContext.ParseColor(props.ColorHex));
    }

    void SetMeasureFont(SparkleFont font)
    {
        measurePage.Graphics.Font = font;
    }

    // ---- Layout ----

    static double Indent(ParagraphElement paragraph) => paragraph.Properties.LeftIndentPoints;

    // Right indent narrows the wrap width the same way the left indent does — and a NEGATIVE right
    // indent (common in resume / multi-column templates) WIDENS it past the normal content edge.
    // The Skia/ImageSharp backends subtract it from the wrap width; the Sparkle backend used to drop
    // it, so right-indented paragraphs wrapped at a Word-divergent width.
    static double RightIndent(ParagraphElement paragraph) => paragraph.Properties.RightIndentPoints;

    // The first line's signed offset from the left indent. A positive first-line indent (w:firstLine)
    // pushes it right; a hanging indent (w:hanging, mutually exclusive with w:firstLine) OUTDENTS it
    // left to L - hanging — but only for a markerless paragraph. A numbered/bulleted list keeps its
    // first-line TEXT at the left indent and hangs the marker into the gap instead (drawn separately),
    // so the outdent must not apply there. Word draws a "bibliography" hanging paragraph's first line
    // at the margin (L - hanging) with continuation lines at L; the raster leaves the first line at L
    // (and over-indents continuation), so this deliberately diverges from the raster to match Word.
    static double FirstLineOffset(ParagraphElement paragraph) =>
        paragraph.Properties.FirstLineIndentPoints -
        (paragraph.Properties.Numbering == null ? paragraph.Properties.HangingIndentPoints : 0);

    float MeasureString(string text, SparkleFont font)
    {
        SetMeasureFont(font);
        return measurePage.Graphics.StringWidth(text);
    }

    float MeasureFollowingWidth(IReadOnlyList<Run> runs, int startIndex)
    {
        var total = 0f;
        for (var i = startIndex; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.IsTab) break;
            if (run.InlineImageData is { Length: > 0 })
            {
                total += run.InlineImageWidthPoints > 0 ? (float)run.InlineImageWidthPoints : 12f;
                continue;
            }
            if (run.Text.Contains('\n') || run.Text.Contains('\r')) break;
            var text = run.Properties.AllCaps ? run.Text.ToUpperInvariant() : run.Text;
            total += MeasureString(text, context.GetFont(run.Properties));
        }
        return total;
    }

    float? MeasureFollowingDecimalPrefix(IReadOnlyList<Run> runs, int startIndex)
    {
        var total = 0f;
        for (var i = startIndex; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.IsTab || run.InlineImageData is { Length: > 0 } || run.Text.Contains('\n') || run.Text.Contains('\r'))
                break;
            var text = run.Properties.AllCaps ? run.Text.ToUpperInvariant() : run.Text;
            var font = context.GetFont(run.Properties);
            var dotIndex = text.IndexOf('.');
            if (dotIndex >= 0)
                return total + MeasureString(text[..dotIndex], font);
            total += MeasureString(text, font);
        }
        return null;
    }

    static int SkipFollowingTabContent(IReadOnlyList<Run> runs, int tabRunIndex)
    {
        var last = tabRunIndex;
        for (var i = tabRunIndex + 1; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.IsTab || (!string.IsNullOrEmpty(run.Text) && (run.Text.Contains('\n') || run.Text.Contains('\r'))))
                break;
            last = i;
        }
        return last;
    }

    // A table-cell paragraph is laid out ~5x (autofit natural + minimum width, row height,
    // vertical-align measure, draw); positioned frames 3x. The layout is pure per
    // (paragraph, width), so memoize it for the engine's lifetime (one document render).
    // Mirrors PdfTextEngine.layoutCache.
    readonly Dictionary<(ParagraphElement Paragraph, float Width), List<Line>> layoutCache = [];

    List<Line> Layout(ParagraphElement paragraph, float availableWidth)
    {
        // The autofit minimum-width probe (1pt) produces a line per word that is only reduced
        // to a max; keep those out of the cache instead of retaining them for the render.
        var cacheable = availableWidth > 1;
        var cacheKey = (paragraph, availableWidth);
        if (cacheable && layoutCache.TryGetValue(cacheKey, out var cachedLines))
            return cachedLines;

        var lines = new List<Line>();
        if (availableWidth <= 0) availableWidth = 1;

        var multiplier = paragraph.Properties.LineSpacingRule == LineSpacingRule.Auto
            ? (float)paragraph.Properties.LineSpacingMultiplier : 1f;

        // Word's line-spacing rules beyond Auto: Exactly forces the specified pitch (smaller or
        // larger than natural), AtLeast is a floor. Applied per finished line so the tallest run
        // still wins under AtLeast. Mirrors PdfTextEngine.
        float ApplyLineSpacingRule(float naturalHeight) =>
            paragraph.Properties.LineSpacingRule switch
            {
                LineSpacingRule.Exactly => (float)paragraph.Properties.LineSpacingPoints,
                LineSpacingRule.AtLeast => Math.Max(naturalHeight, (float)paragraph.Properties.LineSpacingPoints),
                _ => naturalHeight
            };

        var leftIndent = (float)Indent(paragraph);

        // Each line wraps at EffectiveWidth() — the wrap width the caller derived by removing the
        // paragraph's left/right indents, adjusted on the FIRST line by its signed offset (see
        // FirstLineOffset): a first-line indent narrows it; a markerless hanging indent outdents the
        // line so it wraps that much WIDER. Draw shifts the first line's start to match.
        //
        // Continuation lines are NOT shifted for a hanging indent: the Sparkle backend draws them at
        // the left indent (where Word puts them), whereas the raster shifts them a further
        // hanging-indent right — a raster bug, so matching it would regress.
        var firstLineIndent = (float)FirstLineOffset(paragraph);
        float EffectiveWidth() => lines.Count == 0 ? availableWidth - firstLineIndent : availableWidth;

        var current = new Line();
        var pendingSpaceWidth = 0f;
        SparkleFont? pendingSpaceFont = null;
        RunProperties? pendingSpaceProps = null;

        void Flush()
        {
            if (current.Items.Count > 0)
            {
                current.Height = ApplyLineSpacingRule(current.Height);
                lines.Add(current);
            }
            current = new Line();
            pendingSpaceWidth = 0; pendingSpaceFont = null; pendingSpaceProps = null;
        }

        void Account(LineItem item)
        {
            current.Items.Add(item);
            current.Width += item.Width;
            current.Ascent = Math.Max(current.Ascent, item.Ascent);
            current.Height = Math.Max(current.Height, item.Height);
        }

        for (var runIndex = 0; runIndex < paragraph.Runs.Count; runIndex++)
        {
            var run = paragraph.Runs[runIndex];
            if (run.Properties.Hidden) continue;

            if (run.InlineImageData != null || run.InlineImageRasterFallbackData != null)
            {
                var data = run.InlineImageContentType == "image/svg+xml"
                    ? run.InlineImageRasterFallbackData
                    : run.InlineImageData ?? run.InlineImageRasterFallbackData;
                if (data == null) continue;
                var w = run.InlineImageWidthPoints > 0 ? (float)run.InlineImageWidthPoints : 12f;
                var h = run.InlineImageHeightPoints > 0 ? (float)run.InlineImageHeightPoints : 12f;
                if (current.Items.Count > 0 && current.Width + pendingSpaceWidth + w > EffectiveWidth())
                    Flush();
                Account(new LineItem { IsImage = true, ImageData = data, ImageWidth = w, ImageHeight = h, Width = w, Ascent = h, Height = h });
                continue;
            }

            var font = context.GetFont(run.Properties);
            var (_, ascent, rawHeight) = Metrics(font);
            var lineHeight = rawHeight * multiplier;

            if (run.IsTab)
            {
                if (current.Items.Count > 0)
                {
                    if (pendingSpaceWidth > 0)
                    {
                        Account(new LineItem
                        {
                            Text = " ", Props = pendingSpaceProps ?? run.Properties, Font = pendingSpaceFont ?? font,
                            Width = pendingSpaceWidth, IsSpace = true,
                            Ascent = AscentOf(pendingSpaceFont ?? font), Height = HeightOf(pendingSpaceFont ?? font, multiplier)
                        });
                        current.SpaceCount++;
                        pendingSpaceWidth = 0; pendingSpaceFont = null; pendingSpaceProps = null;
                    }

                    var cursorFromLeft = (double)(leftIndent + current.Width);
                    var decimalPrefix = paragraph.Properties.HasDecimalTabStop()
                        ? (double?)MeasureFollowingDecimalPrefix(paragraph.Runs, runIndex + 1) : null;
                    var (destination, matchedStop, suppressFollowing) = TabStopResolver.Resolve(
                        cursorFromLeft, () => MeasureFollowingWidth(paragraph.Runs, runIndex + 1),
                        paragraph.Properties.TabStops, paragraph.Properties.DefaultTabStopPoints,
                        leftIndent, decimalPrefix, availableEndX: leftIndent + EffectiveWidth());
                    var gap = (float)(destination - cursorFromLeft);
                    if (gap > 0 && current.Width + gap <= EffectiveWidth())
                    {
                        Account(new LineItem
                        {
                            Text = "", Props = run.Properties, Font = font, Width = gap,
                            IsTabFiller = true, TabLeader = matchedStop?.Leader ?? TabLeader.None,
                            Ascent = ascent, Height = lineHeight
                        });
                    }
                    if (suppressFollowing)
                        runIndex = SkipFollowingTabContent(paragraph.Runs, runIndex);
                }
                continue;
            }

            var text = run.Properties.AllCaps ? run.Text.ToUpperInvariant() : run.Text;
            foreach (var token in Tokenize(text))
            {
                if (token.IsSpace)
                {
                    var breakCount = token.Text.Count(c => c == '\n');
                    if (breakCount > 0)
                    {
                        for (var i = 0; i < breakCount; i++)
                        {
                            if (current.Items.Count > 0) Flush();
                            else { lines.Add(new Line { Ascent = ascent, Height = ApplyLineSpacingRule(lineHeight) }); pendingSpaceWidth = 0; pendingSpaceFont = null; pendingSpaceProps = null; }
                        }
                        continue;
                    }
                    pendingSpaceWidth += Metrics(font).SpaceWidth * (float)token.Text.Length;
                    pendingSpaceFont = font; pendingSpaceProps = run.Properties;
                    continue;
                }

                var wordWidth = MeasureString(token.Text, font);
                if (current.Items.Count > 0 && current.Width + pendingSpaceWidth + wordWidth > EffectiveWidth())
                    Flush();
                else if (pendingSpaceWidth > 0 && current.Items.Count > 0)
                {
                    Account(new LineItem
                    {
                        Text = " ", Props = pendingSpaceProps ?? run.Properties, Font = pendingSpaceFont ?? font,
                        Width = pendingSpaceWidth, IsSpace = true,
                        Ascent = AscentOf(pendingSpaceFont ?? font), Height = HeightOf(pendingSpaceFont ?? font, (float)multiplier)
                    });
                    current.SpaceCount++;
                }
                pendingSpaceWidth = 0; pendingSpaceFont = null; pendingSpaceProps = null;
                Account(new LineItem { Text = token.Text, Props = run.Properties, Font = font, Width = wordWidth, Ascent = ascent, Height = lineHeight });
            }
        }

        Flush();
        if (lines.Count > 0) lines[^1].IsLast = true;

        if (cacheable)
            layoutCache[cacheKey] = lines;

        return lines;
    }

    float AscentOf(SparkleFont font) => Metrics(font).Ascent;

    float HeightOf(SparkleFont font, float multiplier) => Metrics(font).RawHeight * multiplier;

    static IEnumerable<Token> Tokenize(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var isSpace = char.IsWhiteSpace(text[index]);
            var start = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]) == isSpace) index++;
            yield return new Token(isSpace, text[start..index]);
        }
    }

    readonly record struct Token(bool IsSpace, string Text);

    sealed class LineItem
    {
        public string? Text;
        public RunProperties Props = new();
        public SparkleFont Font = null!;
        public float Width;
        public float Ascent;
        public float Height;
        public bool IsSpace;
        public bool IsImage;
        public byte[]? ImageData;
        public float ImageWidth;
        public float ImageHeight;
        public bool IsTabFiller;
        public TabLeader TabLeader;
    }

    sealed class Line
    {
        public List<LineItem> Items { get; } = [];
        public float Width;
        public float Ascent;
        public float Height;
        public bool IsLast;
        public int SpaceCount;
    }
}
