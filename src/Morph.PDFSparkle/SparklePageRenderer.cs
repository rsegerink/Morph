namespace Morph;

/// <summary>
/// Drives the shared PageRendererBase layout engine onto Sparkle pages.
/// All table, pagination, form-field, content-control and header/footer logic is inherited;
/// this class supplies the Sparkle drawing primitives and the document-level render loop.
/// </summary>
sealed class SparklePageRenderer : PageRendererBase
{
    readonly SparkleRenderContext context;
    readonly SparkleTextEngine textEngine;

    int pagesAdded;
    IPage? currentPage;
    bool hasSignificantContentOnCurrentPage;
    bool currentPageFromExplicitBreak;

    // Track whether the current page was started by a section break (a new page setup): Word
    // keeps a paragraph's spacing-before at the top of such pages in every mode.
    bool currentPageFromSectionBreak;

    // When set, rendering stops once every page in the range is complete: layout is strictly
    // forward, so once the page in progress is past Pages.End everything that follows lands on
    // pages a trim step would delete anyway. Unlike PdfPageRenderer, Sparkle has no public API to
    // remove already-created pages from the document, so only the early-exit (skip rendering
    // past the requested range) applies here — pages before Pages.Start still appear in the
    // output. Requesting page 1 of a 500-page document still avoids laying out and drawing all 500.
    public PageRange? Pages { get; init; }

    public Action<ExportWarning>? OnWarning { get; init; }

    public SparklePageRenderer(SparkleRenderContext context) : base(context)
    {
        this.context = context;
        textEngine = new SparkleTextEngine(context)
        {
            RequestNewPage = () =>
            {
                // Flow into the next column of a multi-column section before spilling to a new
                // page. For single-column sections MoveToNextColumn returns false and this is an
                // ordinary page break. Mirrors PdfPageRenderer.
                if (!context.MoveToNextColumn())
                {
                    FinishCurrentPage();
                    StartNewPage();
                }
            }
        };
    }

    protected override IParagraphMeasurer Measurer => textEngine;
    protected override bool HasOutput => context.Graphics != null;

    IGraphics Graphics => context.Graphics!;

    public int RenderDocument(ParsedDocument document)
    {
        header             = document.Header;
        footer             = document.Footer;
        firstPageHeader    = document.FirstPageHeader;
        firstPageFooter    = document.FirstPageFooter;
        evenPageHeader     = document.EvenPageHeader;
        evenPageFooter     = document.EvenPageFooter;
        differentFirstPage = document.PageSettings.DifferentFirstPage;

        context.SetHeaderFooterSpace(0, 0);
        context.InitializeLineNumbers();
        StartNewPage();

        var elements = document.Elements;
        for (var index = 0; index < elements.Count; index++)
        {
            // pagesAdded counts pages started, so once it passes the requested range's end the
            // page in progress (and everything after it) would be trimmed anyway.
            if (Pages is { } range && pagesAdded > range.End)
                break;

            var element = elements[index];

            if (element is FloatingShapeElement { BehindText: true } bg)
            {
                AdvanceToBackgroundsTargetPage(elements, index);
                RenderBackgroundShape(bg);
                continue;
            }
            if (element is FloatingImageElement { BehindText: true } bgImg)
            {
                AdvanceToBackgroundsTargetPage(elements, index);
                RenderFloatingImage(bgImg);
                continue;
            }

            DocumentElement? nextElement = null;
            for (var la = index + 1; la < elements.Count; la++)
            {
                if (elements[la] is FloatingShapeElement { BehindText: true } or FloatingImageElement { BehindText: true })
                    continue;
                nextElement = elements[la];
                break;
            }

            RenderElement(element, nextElement);
        }

        FinishCurrentPage();
        RemoveBlankTrailingPage();
        return pagesAdded;
    }

    void RenderElement(DocumentElement element, DocumentElement? nextElement)
    {
        switch (element)
        {
            case PageBreakElement:
                FinishCurrentPage(); StartNewPage();
                currentPageFromExplicitBreak = true;
                break;
            case ColumnBreakElement:
                if (!context.MoveToNextColumn()) { FinishCurrentPage(); StartNewPage(); currentPageFromExplicitBreak = true; }
                break;
            case SectionBreakElement sectionBreak:
                if (sectionBreak.NewSectionSettings != null)
                    context.UpdatePageSettings(sectionBreak.NewSectionSettings);
                if (context.CurrentY > context.ContentTop)
                {
                    FinishCurrentPage(); StartNewPage();
                    currentPageFromExplicitBreak = true;
                    currentPageFromSectionBreak = true;
                }
                break;
            case ParagraphElement paragraph:
                RenderParagraph(paragraph, nextElement);
                break;
            case HorizontalRuleElement:
                RenderHorizontalRule();
                hasSignificantContentOnCurrentPage = true;
                break;
            case ImageElement image:
                RenderImage(image);
                hasSignificantContentOnCurrentPage = true;
                break;
            case FloatingImageElement floatingImage:
                RenderFloatingImage(floatingImage);
                hasSignificantContentOnCurrentPage = true;
                break;
            case FloatingTextBoxElement textBox:
                RenderFloatingTextBox(textBox);
                hasSignificantContentOnCurrentPage = true;
                break;
            case PositionedFrameElement frame:
                RenderPositionedFrame(frame);
                hasSignificantContentOnCurrentPage = true;
                break;
            case TableElement table:
                RenderTable(table);
                hasSignificantContentOnCurrentPage = true;
                context.LastParagraphSpacingAfterPoints = 0;
                context.LastParagraphHadContextualSpacing = false;
                context.LastParagraphStyleId = null;
                break;
            case WordArtElement wordArt:
                RenderWordArtBlock(wordArt);
                hasSignificantContentOnCurrentPage = true;
                break;
            case TextFormFieldElement textField:
                RenderTextFormField(textField);
                hasSignificantContentOnCurrentPage = true;
                break;
            case CheckBoxFormFieldElement checkBox:
                RenderCheckBoxFormField(checkBox);
                hasSignificantContentOnCurrentPage = true;
                break;
            case DropDownFormFieldElement dropDown:
                RenderDropDownFormField(dropDown);
                hasSignificantContentOnCurrentPage = true;
                break;
            case ContentControlElement contentControl:
                RenderContentControl(contentControl);
                hasSignificantContentOnCurrentPage = true;
                break;
            case InkElement:
            case FloatingShapeElement:
            case FloatingWordArtElement:
                OnWarning?.Invoke(new(WarningKind.UnsupportedElement,
                    $"{element.GetType().Name} is not rendered by the Sparkle backend and was dropped."));
                break;
        }
    }

    void RenderTextAsParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RenderParagraph(new() { Runs = [new() { Text = text, Properties = new() }], Properties = new() });
    }

    // The Sparkle backend draws WordArt as its plain text (no glyph warps), but it must still
    // occupy the shape's declared block height so pagination lines up with Word and the raster
    // backends. Without this a tall section of WordArt shapes collapses to a few text lines and
    // the pages Word spreads them across are lost. Mirrors PdfPageRenderer.RenderWordArtBlock.
    void RenderWordArtBlock(WordArtElement wordArt)
    {
        var height = (float)wordArt.HeightPoints;
        if (height > 0)
            EnsureSpaceFor(height);

        var startY = context.CurrentY;
        RenderTextAsParagraph(wordArt.Text);
        context.CurrentY = Math.Max(context.CurrentY, startY + height);
    }

    void RenderFloatingTextBox(FloatingTextBoxElement textBox)
    {
        if (!HasOutput) return;

        var bounds = FloatingPosition.ResolveBounds(context,
            textBox.HorizontalAnchor, textBox.VerticalAnchor,
            textBox.HorizontalPositionPoints, textBox.VerticalPositionPoints,
            textBox.WidthPoints, textBox.HeightPoints,
            textBox.HorizontalPositionPercent, textBox.VerticalPositionPercent);

        if (textBox.BackgroundColorHex != null)
            Graphics.FillRectangle(bounds.X, bounds.Y, bounds.PixelWidth, bounds.PixelHeight);

        var savedY = context.CurrentY;
        context.CurrentY = bounds.Y;
        foreach (var el in textBox.Content)
        {
            if (el is ParagraphElement para)
                RenderParagraphInBounds(para, bounds.X, (float)textBox.WidthPoints);
        }
        context.CurrentY = savedY;
    }

    // ---- Page lifecycle ----

    protected override void StartNewPage()
    {
        currentPage = context.Document.AddPage(
            (float)context.PageSettings.WidthPoints,
            (float)context.PageSettings.HeightPoints);

        context.Graphics = currentPage.Graphics;

        var background = context.PageSettings.BackgroundColorHex;
        if (!string.IsNullOrEmpty(background))
        {
            Graphics.Brush = context.GetBrush(SparkleRenderContext.ParseColor(background));
            Graphics.FillRectangle(0, 0, (float)context.PageSettings.WidthPoints, (float)context.PageSettings.HeightPoints);
        }

        DrawPageBorders();

        if (pagesAdded > 0)
        {
            context.StartNewPage();
            context.ResetLineNumbersForPage();
        }

        pagesAdded++;
        RenderHeader();
        hasSignificantContentOnCurrentPage = false;
        currentPageFromExplicitBreak = false;
        currentPageFromSectionBreak = false;
    }

    protected override void FinishCurrentPage()
    {
        if (currentPage == null) return;
        RenderFooter();
        context.Graphics?.Flush();
        context.Graphics = null;
        currentPage = null;
    }

    void RemoveBlankTrailingPage()
    {
        // Sparkle doesn't support removing pages after creation; just track the count.
        if (pagesAdded > 1 && !hasSignificantContentOnCurrentPage && !currentPageFromExplicitBreak)
            pagesAdded--;
    }

    void DrawPageBorders()
    {
        if (context.PageSettings.PageBorders is not { HasAnyBorder: true } borders) return;

        var w = (float)context.PageSettings.WidthPoints;
        var h = (float)context.PageSettings.HeightPoints;
        var left   = (float)borders.LeftSpacePoints;
        var right  = w - (float)borders.RightSpacePoints;
        var top    = (float)borders.TopSpacePoints;
        var bottom = h - (float)borders.BottomSpacePoints;

        if (borders.Top.IsVisible)    { SetEdgePen(borders.Top);    Graphics.DrawLine(left, top, right, top); }
        if (borders.Bottom.IsVisible) { SetEdgePen(borders.Bottom); Graphics.DrawLine(left, bottom, right, bottom); }
        if (borders.Left.IsVisible)   { SetEdgePen(borders.Left);   Graphics.DrawLine(left, top, left, bottom); }
        if (borders.Right.IsVisible)  { SetEdgePen(borders.Right);  Graphics.DrawLine(right, top, right, bottom); }
    }

    void SetEdgePen(BorderEdge edge) =>
        Graphics.Pen = context.GetPen(SparkleRenderContext.ParseColor(edge.ColorHex ?? "000000"), Math.Max(0.5f, (float)edge.WidthPoints));

    // ---- Drawing primitives required by PageRendererBase ----

    protected override void RenderParagraph(ParagraphElement paragraph, DocumentElement? nextElement = null)
    {
        var hasContent = false;
        var isEmpty = paragraph.Runs.Count == 0;
        foreach (var run in paragraph.Runs)
        {
            if (run.InlineImageData != null || !string.IsNullOrWhiteSpace(run.Text)) { hasContent = true; break; }
        }

        if (paragraph.Properties.PageBreakBefore && !isEmpty && context.CurrentY > context.ContentTop)
        {
            FinishCurrentPage(); StartNewPage();
            currentPageFromExplicitBreak = true;
        }

        // Float wrap: a paragraph starting beside a wrap-enabled floating image lays out inside
        // the widest free band next to it; a wrapTopAndBottom float advances Y below itself.
        var (bandX, bandWidth, bandY, bandConstrained) = context.ResolveFlowBand(context.CurrentY);
        if (bandY > context.CurrentY)
            context.CurrentY = bandY;

        using (bandConstrained ? context.PushContentContainer(bandX, bandWidth) : null)
        {
            // KeepNext / KeepLines with Word's abandonment guards: no push when already at the
            // top of the page (pushing again cannot help) and no push when the kept content
            // cannot fit a fresh column either. Mirrors PdfPageRenderer.
            if (paragraph.Properties.KeepNext && nextElement != null && !isEmpty)
            {
                var nextHeight = MeasureElementHeight(nextElement, SparkleTextEngine.SpacingAfter(paragraph));
                if (nextHeight > 0)
                {
                    var combinedHeight = textEngine.MeasureFlowHeight(paragraph, context.ContentWidth, context.LastParagraphSpacingAfterPoints) + nextHeight;
                    if (!context.HasSpaceFor(combinedHeight) &&
                        combinedHeight <= context.ContentHeight &&
                        context.CurrentY > context.ContentTop)
                    {
                        AdvanceToNextColumnOrPage();
                    }
                }
            }

            if (paragraph.Properties.KeepLines && !isEmpty)
            {
                var height = textEngine.MeasureFlowHeight(paragraph, context.ContentWidth, context.LastParagraphSpacingAfterPoints);
                if (!context.HasSpaceFor(height) &&
                    height <= context.ContentHeight &&
                    context.CurrentY > context.ContentTop)
                {
                    AdvanceToNextColumnOrPage();
                }
            }

            context.SuppressPageTopSpacingBefore = ShouldSuppressPageTopSpacingBefore();
            textEngine.Render(paragraph);
        }

        if (hasContent) hasSignificantContentOnCurrentPage = true;
    }

    // Height of the element a KeepNext paragraph must share its page with, charged the way the
    // flow will charge it (a following paragraph collapses its spacing-before against this
    // paragraph's spacing-after). Tables return 0 (keep-before-table stays inert until it has a
    // table pre-measure), mirroring PdfPageRenderer.
    float MeasureElementHeight(DocumentElement element, float previousSpacingAfter) =>
        element switch
        {
            ParagraphElement para => textEngine.MeasureFlowHeight(para, context.ContentWidth, previousSpacingAfter),
            ImageElement img => (float)img.HeightPoints,
            _ => 0
        };

    // Word does not apply a body paragraph's spacing-before at the top of a page reached by an
    // automatic break; a section break (a new page setup) and the document's first page keep it.
    // Column tops are left unchanged. Mirrors PdfPageRenderer.
    bool ShouldSuppressPageTopSpacingBefore()
    {
        if (pagesAdded <= 1 ||
            context.CurrentColumn != 0 ||
            context.CurrentY > context.ContentTop + 0.01f)
        {
            return false;
        }

        if (currentPageFromSectionBreak)
            return false;

        if (currentPageFromExplicitBreak)
            return context.Compatibility.CompatibilityMode >= 15;

        return true;
    }

    protected override void RenderParagraphInBounds(ParagraphElement paragraph, float x, float maxWidth)
    {
        if (HasOutput) textEngine.RenderInBounds(paragraph, x, maxWidth);
    }

    protected override void RenderHeaderFooterParagraph(ParagraphElement paragraph)
    {
        if (HasOutput) textEngine.RenderInBounds(paragraph, context.ContentLeft, context.ContentWidth);
    }

    protected override void RenderImageInCell(ImageElement image, float x, float maxWidth)
    {
        if (!HasOutput) return;
        var iw = (float)image.WidthPoints;
        var ih = (float)image.HeightPoints;
        if (iw > maxWidth) { ih *= maxWidth / iw; iw = maxWidth; }
        DrawRaster(image.ImageData, image.ContentType, image.RasterFallbackData, image.RasterFallbackContentType, x, context.CurrentY, iw, ih);
        context.CurrentY += ih;
    }

    protected override void RenderVerticalCellContent(TableCell cell, float cellX, float cellY, float cellWidth, float cellHeight, CellSpacing padding)
    {
        if (!HasOutput) return;
        var contentX = cellX + (float)padding.Left;
        var contentY = cellY + (float)padding.Top;
        var availH = cellHeight - (float)padding.Vertical;

        var bottomToTop = cell.Properties.TextDirection == CellTextDirection.BottomToTop;
        Graphics.SaveState();
        if (bottomToTop)
            Graphics.Translate(contentX, contentY + availH);
        else
            Graphics.Translate(contentX + (cellWidth - (float)padding.Horizontal), contentY);
        Graphics.Rotate(0, 0, bottomToTop ? -90 : 90);

        var savedY = context.CurrentY;
        context.CurrentY = 0;
        foreach (var el in cell.Content)
        {
            if (el is ParagraphElement para) RenderParagraphInBounds(para, 0, availH);
        }
        context.CurrentY = savedY;
        Graphics.RestoreState();
    }

    protected override void DrawCellBackground(float pixelX, float pixelY, float pixelWidth, float pixelHeight, string hexColor)
    {
        if (!HasOutput) return;
        Graphics.Brush = context.GetBrush(SparkleRenderContext.ParseColor(hexColor));
        Graphics.FillRectangle(pixelX, pixelY, pixelWidth, pixelHeight);
    }

    protected override void DrawCellBorders(float pixelX, float pixelY, float pixelWidth, float pixelHeight, CellBorders borders)
    {
        if (!HasOutput) return;
        if (borders.Top.IsVisible)    { SetEdgePen(borders.Top);    Graphics.DrawLine(pixelX, pixelY, pixelX + pixelWidth, pixelY); }
        if (borders.Right.IsVisible)  { SetEdgePen(borders.Right);  Graphics.DrawLine(pixelX + pixelWidth, pixelY, pixelX + pixelWidth, pixelY + pixelHeight); }
        if (borders.Bottom.IsVisible) { SetEdgePen(borders.Bottom); Graphics.DrawLine(pixelX, pixelY + pixelHeight, pixelX + pixelWidth, pixelY + pixelHeight); }
        if (borders.Left.IsVisible)   { SetEdgePen(borders.Left);   Graphics.DrawLine(pixelX, pixelY, pixelX, pixelY + pixelHeight); }
    }

    protected override void DrawCellDiagonals(float pixelX, float pixelY, float pixelWidth, float pixelHeight, CellDiagonals diagonals)
    {
        if (!HasOutput) return;
        if (diagonals.Down.IsVisible) { SetEdgePen(diagonals.Down); Graphics.DrawLine(pixelX, pixelY, pixelX + pixelWidth, pixelY + pixelHeight); }
        if (diagonals.Up.IsVisible)   { SetEdgePen(diagonals.Up);   Graphics.DrawLine(pixelX + pixelWidth, pixelY, pixelX, pixelY + pixelHeight); }
    }

    protected override void DrawFormFieldRect(float pixelX, float pixelY, float pixelWidth, float pixelHeight, string fillHex, string borderHex, float pixelBorderWidth)
    {
        if (!HasOutput) return;
        Graphics.Brush = context.GetBrush(SparkleRenderContext.ParseColor(fillHex));
        Graphics.FillRectangle(pixelX, pixelY, pixelWidth, pixelHeight);
        Graphics.Pen = context.GetPen(SparkleRenderContext.ParseColor(borderHex), Math.Max(0.5f, pixelBorderWidth));
        Graphics.DrawRectangle(pixelX, pixelY, pixelWidth, pixelHeight);
    }

    protected override void DrawFormFieldText(string text, float pixelX, float pixelY, float pixelWidth, float pixelHeight, string textHex)
    {
        if (!HasOutput) return;
        var font = context.GetFont(DefaultFontSettings.DefaultFont, false, false, 10);
        Graphics.Font = font;
        Graphics.Brush = context.GetBrush(SparkleRenderContext.ParseColor(textHex));
        var metrics = Graphics.FontMetrics;
        var baseline = pixelY + (pixelHeight - metrics.Height) / 2 + metrics.Ascent;
        Graphics.DrawString(pixelX + 3, baseline, text);
    }

    protected override void DrawCheckMark(float pixelX, float pixelY, float pixelSize, string hexColor, float pixelStrokeWidth, bool xShape)
    {
        if (!HasOutput) return;
        Graphics.Pen = context.GetPen(SparkleRenderContext.ParseColor(hexColor), Math.Max(0.6f, pixelStrokeWidth));
        if (xShape)
        {
            Graphics.DrawLine(pixelX + pixelSize * 0.2f, pixelY + pixelSize * 0.2f, pixelX + pixelSize * 0.8f, pixelY + pixelSize * 0.8f);
            Graphics.DrawLine(pixelX + pixelSize * 0.8f, pixelY + pixelSize * 0.2f, pixelX + pixelSize * 0.2f, pixelY + pixelSize * 0.8f);
        }
        else
        {
            Graphics.DrawLine(pixelX + pixelSize * 0.2f, pixelY + pixelSize * 0.55f, pixelX + pixelSize * 0.42f, pixelY + pixelSize * 0.78f);
            Graphics.DrawLine(pixelX + pixelSize * 0.42f, pixelY + pixelSize * 0.78f, pixelX + pixelSize * 0.82f, pixelY + pixelSize * 0.25f);
        }
    }

    protected override void DrawDropDownArrow(float pixelX, float pixelY, float pixelHeight, string hexColor)
    {
        if (!HasOutput) return;
        var size = pixelHeight * 0.3f;
        var cx = pixelX - size;
        var cy = pixelY + pixelHeight / 2;
        var pts = new PointCollection();
        pts.Add(cx - size / 2, cy - size / 4);
        pts.Add(cx + size / 2, cy - size / 4);
        pts.Add(cx, cy + size / 2);
        Graphics.Brush = context.GetBrush(SparkleRenderContext.ParseColor(hexColor));
        Graphics.FillPolygon(pts);
    }

    protected override void DrawHorizontalRuleLine(float pixelX1, float pixelY, float pixelX2, string hexColor, float pixelStrokeWidth)
    {
        if (!HasOutput) return;
        Graphics.Pen = context.GetPen(SparkleRenderContext.ParseColor(hexColor), Math.Max(0.4f, pixelStrokeWidth));
        Graphics.DrawLine(pixelX1, pixelY, pixelX2, pixelY);
    }

    protected override void RenderBackgroundShape(FloatingShapeElement shape)
    {
        if (!HasOutput) return;

        var (width, height) = FloatingPosition.ResolveEffectiveSize(context,
            shape.WidthPoints, shape.HeightPoints, shape.WidthPercent, shape.WidthRelativeFrom,
            shape.HeightPercent, shape.HeightRelativeFrom);

        var bounds = FloatingPosition.ResolveShapeBounds(context,
            shape.HorizontalAnchor, shape.VerticalAnchor,
            shape.HorizontalPositionPoints, shape.VerticalPositionPoints,
            width, height, shape.HorizontalPositionPercent, shape.VerticalPositionPercent);

        var x = bounds.PixelX; var y = bounds.PixelY;
        var sw = bounds.PixelWidth; var sh = bounds.PixelHeight;

        if (shape.ImageData != null)
        {
            DrawRaster(shape.ImageData, shape.ImageContentType, null, null, x, y, sw, sh);
        }
        else if (shape.Gradient is { } gradient)
        {
            Graphics.Brush = BuildGradientBrush(gradient);
            FillShape(shape, x, y, sw, sh);
        }
        else if (shape.FillColorHex != null)
        {
            var col = SparkleRenderContext.ParseColor(shape.FillColorHex);
            var alpha = (byte)Math.Round(Math.Clamp(shape.FillAlpha, 0, 1) * 255);
            Graphics.Brush = context.GetBrush(new Color(alpha, col.R, col.G, col.B));
            FillShape(shape, x, y, sw, sh);
        }

        if (shape is { LineColorHex: { } lineColor, LineWidthPoints: { } lineWidth and > 0 })
        {
            Graphics.Pen = context.GetPen(SparkleRenderContext.ParseColor(lineColor), Math.Max(0.4f, (float)lineWidth));
            StrokeShape(shape, x, y, sw, sh);
        }
    }

    void FillShape(FloatingShapeElement shape, float x, float y, float width, float height)
    {
        if (shape.Subpaths != null)
        {
            BuildShapePath(shape, x, y, width, height, PaintMode.Fill);
        }
        else if (shape.Preset == PresetShape.Ellipse)
        {
            Graphics.FillEllipse(x, y, width, height);
        }
        else
        {
            Graphics.FillRectangle(x, y, width, height);
        }
    }

    void StrokeShape(FloatingShapeElement shape, float x, float y, float width, float height)
    {
        if (shape.Subpaths != null)
        {
            BuildShapePath(shape, x, y, width, height, PaintMode.Stroke);
        }
        else if (shape.Preset == PresetShape.Ellipse)
        {
            Graphics.DrawEllipse(x, y, width, height);
        }
        else
        {
            Graphics.DrawRectangle(x, y, width, height);
        }
    }

    // Builds a path from custom geometry: each sub-path is its own closed contour, filled with
    // nonzero winding so oppositely-wound nested contours read as holes (matching DrawingML's
    // default custGeom fill) rather than fusing into one polygon. Mirrors PdfPageRenderer.
    void BuildShapePath(FloatingShapeElement shape, float x, float y, float width, float height, PaintMode mode)
    {
        // Flip in the unit square, scale into the bounding box, then rotate around its centre —
        // matching the Skia/ImageSharp/Pdf path transform so rotated custom geometry lines up.
        var centerX = x + width / 2;
        var centerY = y + height / 2;
        var radians = shape.RotationDegrees * Math.PI / 180.0;
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);

        Graphics.AlternateFill = false;
        Graphics.StartShape(mode);
        foreach (var contour in shape.Subpaths!)
        {
            for (var i = 0; i < contour.Count; i++)
            {
                var (pointX, pointY) = contour[i];
                var unitX = shape.FlipHorizontal ? 1 - pointX : pointX;
                var unitY = shape.FlipVertical ? 1 - pointY : pointY;
                var absoluteX = x + (float)unitX * width;
                var absoluteY = y + (float)unitY * height;
                if (shape.RotationDegrees != 0)
                {
                    var deltaX = absoluteX - centerX;
                    var deltaY = absoluteY - centerY;
                    absoluteX = centerX + deltaX * cos - deltaY * sin;
                    absoluteY = centerY + deltaX * sin + deltaY * cos;
                }

                if (i == 0)
                    Graphics.MoveTo(absoluteX, absoluteY);
                else
                    Graphics.LineTo(absoluteX, absoluteY);
            }

            Graphics.ClosePath();
        }
        Graphics.EndShape();
    }

    // Linear gradient mirroring the Skia/ImageSharp/Pdf backends: angle 0deg points along +X,
    // clockwise positive (OOXML a:lin/@ang).
    static LinearGradientBrush BuildGradientBrush(GradientFill gradient) =>
        new(SparkleRenderContext.ParseColor(gradient.StartColorHex), SparkleRenderContext.ParseColor(gradient.EndColorHex), (float)gradient.DirectionDegrees);

    protected override void RenderFloatingImage(FloatingImageElement image)
    {
        if (!HasOutput) return;

        var (width, height) = FloatingPosition.ResolveEffectiveSize(context,
            image.WidthPoints, image.HeightPoints, image.WidthPercent, image.WidthRelativeFrom,
            image.HeightPercent, image.HeightRelativeFrom);

        var bounds = FloatingPosition.ResolveBounds(context,
            image.HorizontalAnchor, image.VerticalAnchor,
            image.HorizontalPositionPoints, image.VerticalPositionPoints,
            width, height, image.HorizontalPositionPercent, image.VerticalPositionPercent);

        // Wrap-enabled floats reserve their footprint so following flow text lays out beside
        // them instead of over them. Mirrors PdfPageRenderer.
        context.RegisterFloatExclusion(image, bounds.X, bounds.Y, (float)width, (float)height);

        DrawRaster(image.ImageData, image.ContentType, image.RasterFallbackData, image.RasterFallbackContentType, bounds.X, bounds.Y, bounds.PixelWidth, bounds.PixelHeight);
    }

    protected override void DrawBlockImage(byte[] imageData, string? contentType, float pixelX, float pixelY, float pixelWidth, float pixelHeight, float rotation, ImageCrop? crop, BlipColorEffect colorEffect)
    {
        if (!HasOutput) return;
        if (Math.Abs(rotation) > 0.01f)
        {
            Graphics.SaveState();
            Graphics.Rotate(pixelX + pixelWidth / 2, pixelY + pixelHeight / 2, rotation);
            DrawRaster(imageData, contentType, null, null, pixelX, pixelY, pixelWidth, pixelHeight);
            Graphics.RestoreState();
        }
        else
        {
            DrawRaster(imageData, contentType, null, null, pixelX, pixelY, pixelWidth, pixelHeight);
        }
    }

    protected override bool CanRenderContentType(string? contentType) => contentType != "image/svg+xml";

    void DrawRaster(byte[] data, string? contentType, byte[]? fallbackData, string? fallbackContentType, float x, float y, float width, float height)
    {
        // Sparkle can't decode SVG (see CanRenderContentType); fall back to the raster blip
        // behind it, but only if that fallback is itself something we can decode. Mirrors
        // PdfPageRenderer.
        if (!CanRenderContentType(contentType))
        {
            if (fallbackData == null || !CanRenderContentType(fallbackContentType))
                return;

            data = fallbackData;
        }

        try
        {
            var image = context.GetImage(data);
            Graphics.DrawImage(image, x, y, width, height);
        }
        catch (Exception exception)
        {
            OnWarning?.Invoke(new(WarningKind.ImageRenderingFailed,
                $"Image ({contentType ?? "unknown content type"}) could not be embedded in the PDF and was dropped: {exception.Message}"));
        }
    }
}
