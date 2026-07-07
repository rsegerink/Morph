namespace Morph;

/// <summary>
/// Rendering state for a Sparkle PDF conversion. Coordinates kept in points (72 DPI) so
/// PointsToPixels is an identity — same convention as the PdfSharp backend.
/// </summary>
sealed class SparkleRenderContext : RenderContextBase
{
    public SparkleDoc Document { get; } = new SparkleDoc();

    public IGraphics? Graphics { get; set; }

    readonly Dictionary<(string Family, bool Bold, bool Italic, float Size), SparkleFont> fontCache = [];
    readonly SparkleFontResolver fontResolver;

    public SparkleRenderContext(
        PageSettings pageSettings,
        CompatibilitySettings? compatibility,
        double fontWidthScale,
        Func<string, string?>? fontFallback,
        string? fontDirectory)
        : base(pageSettings, dpi: 72, compatibility, fontWidthScale, fontFallback, fontDirectory)
    {
        fontResolver = new SparkleFontResolver(fontDirectory);
    }

    public SparkleFont GetFont(RunProperties properties)
    {
        var size = (float)properties.FontSizePoints;
        if (properties.VerticalAlignment != VerticalRunAlignment.Baseline)
            size *= 0.58f;
        return GetFont(properties.FontFamily, properties.Bold, properties.Italic, size);
    }

    public SparkleFont GetFont(string family, bool bold, bool italic, double sizePoints)
    {
        if (sizePoints <= 0) sizePoints = 11;
        var size = (float)sizePoints;
        var key = (family, bold, italic, size);
        if (fontCache.TryGetValue(key, out var cached))
            return cached;

        var path = fontResolver.ResolvePath(family, bold, italic);

        // FontDirectory restricts resolution to the bundled set for deterministic, reproducible
        // output; only fall back to system fonts via Skia when no directory was configured.
        SparkleFont font;
        if (path != null)
        {
            font = new SparkleFont(path, size);
        }
        else
        {
            var skStyle = (bold, italic) switch
            {
                (true, true)  => SkiaSharp.SKFontStyle.BoldItalic,
                (true, false) => SkiaSharp.SKFontStyle.Bold,
                (false, true) => SkiaSharp.SKFontStyle.Italic,
                _             => SkiaSharp.SKFontStyle.Normal
            };

            var typeface = SkiaSharp.SKTypeface.FromFamilyName(family, skStyle)
                ?? SkiaSharp.SKTypeface.FromFamilyName(DefaultFontSettings.DefaultFont, skStyle)
                ?? SkiaSharp.SKTypeface.Default;

            using var skStream = typeface.OpenStream(out _);
            var buffer = new byte[skStream.Length];
            skStream.Read(buffer, buffer.Length);
            font = new SparkleFont(new System.IO.MemoryStream(buffer), size);
        }

        fontCache[key] = font;
        return font;
    }

    public static Color ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex == "auto")
            return new Color(0, 0, 0);
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return new Color((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        return new Color(0, 0, 0);
    }

    // Brushes and pens were allocated per drawn word / underline / border edge. Documents use a
    // small set of colours and widths, so cache by value — Color/SolidColorBrush/Pen are
    // immutable with correct value equality. Mirrors PdfRenderContext.
    readonly Dictionary<Color, SolidColorBrush> brushCache = [];
    readonly Dictionary<(Color Color, float Width), Pen> penCache = [];

    public SolidColorBrush GetBrush(Color color)
    {
        if (!brushCache.TryGetValue(color, out var brush))
        {
            brush = new(color);
            brushCache[color] = brush;
        }

        return brush;
    }

    public Pen GetPen(Color color, float width)
    {
        var key = (color, width);
        if (!penCache.TryGetValue(key, out var pen))
        {
            pen = new(color, width);
            penCache[key] = pen;
        }

        return pen;
    }

    // Sparkle dedupes embedded image XObjects per Image *instance* — a fresh Image from the
    // same bytes is decoded again and embedded again in the output PDF (a header logo on an
    // N-page document used to embed N copies). Cache per source array (reference identity: the
    // parsed elements hold stable arrays). Decode failures propagate to the caller, matching
    // the uncached path — nothing is cached for a throwing source. Mirrors PdfRenderContext.
    readonly Dictionary<byte[], SparkleImage> imageCache = new(ReferenceEqualityComparer.Instance);

    public SparkleImage GetImage(byte[] data)
    {
        if (!imageCache.TryGetValue(data, out var image))
        {
            using var stream = new System.IO.MemoryStream(data);
            image = new SparkleImage(stream);
            imageCache[data] = image;
        }

        return image;
    }
}
