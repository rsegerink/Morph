namespace Morph.Layout;

/// <summary>
/// A box in PDF points, measured from the page's top-left corner, Y downward — the coordinate space
/// Morph lays out in.
/// </summary>
public readonly record struct BoundingBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>A converted document: the PDF and where every word in it was drawn.</summary>
public sealed record DocxLayoutResult(byte[] Pdf, LayoutDocument Layout);

/// <summary>
/// The pages as laid out, plus the paragraphs their lines belong to. A paragraph split over a page
/// break appears once in <see cref="Paragraphs"/>, with lines on both pages.
/// </summary>
public sealed record LayoutDocument(IReadOnlyList<LayoutPage> Pages, IReadOnlyList<LayoutParagraph> Paragraphs);

public sealed record LayoutPage(int Number, double Width, double Height, IReadOnlyList<LayoutLine> Lines);

/// <summary>
/// One drawn line. <see cref="ParagraphIndex"/> indexes <see cref="LayoutDocument.Paragraphs"/>;
/// <see cref="LineIndex"/> is the line's position within its paragraph.
/// </summary>
public sealed record LayoutLine(BoundingBox Box, double Baseline, int ParagraphIndex, int LineIndex, IReadOnlyList<LayoutWord> Words);

/// <summary>
/// A paragraph's structure as the DOCX states it. <see cref="ListLabel"/> is the marker text Morph
/// drew ("1.", "a)", "•"); <see cref="Source"/> is the ordinal of the <c>w:p</c> in the main document part.
/// </summary>
public sealed record LayoutParagraph(int Index, string? StyleId, string? ListLabel, int? ListLevel, int? Source);

/// <summary>
/// A run of non-space characters as drawn, possibly spanning several runs. <see cref="Box"/> runs
/// from the font's ascender to its descender about the baseline the glyphs sit on.
/// <see cref="FontFamily"/> is the face actually drawn with, which differs from the requested family
/// when the host lacks it.
/// </summary>
public sealed record LayoutWord(
    string Text,
    BoundingBox Box,
    double Baseline,
    string FontFamily,
    double FontSizePoints,
    bool Bold,
    bool Italic,
    bool IsListLabel);
