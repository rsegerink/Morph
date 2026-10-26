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
/// The pages as laid out, plus the paragraphs their lines belong to and the tables those sit in. A
/// paragraph split over a page break appears once in <see cref="Paragraphs"/>, with lines on both pages.
/// </summary>
public sealed record LayoutDocument(IReadOnlyList<LayoutPage> Pages, IReadOnlyList<LayoutParagraph> Paragraphs, IReadOnlyList<LayoutTable> Tables);

/// <summary>
/// A <c>w:tbl</c>. <see cref="ParentTable"/> is the table whose cell holds this one, null at the top
/// level. <see cref="ColumnCount"/> counts grid columns.
/// </summary>
public sealed record LayoutTable(int Index, int? ParentTable, int RowCount, int ColumnCount);

/// <summary>
/// Where a paragraph sits in a table, in grid terms: <see cref="Column"/> is the first grid column the
/// cell covers, <see cref="ColumnSpan"/> its <c>w:gridSpan</c>, <see cref="RowSpan"/> the rows a
/// <c>w:vMerge</c> restart covers.
/// </summary>
public sealed record LayoutCellRef(int Table, int Row, int Column, int ColumnSpan, int RowSpan);

public sealed record LayoutPage(int Number, double Width, double Height, IReadOnlyList<LayoutLine> Lines);

/// <summary>
/// One drawn line. <see cref="ParagraphIndex"/> indexes <see cref="LayoutDocument.Paragraphs"/>;
/// <see cref="LineIndex"/> is the line's position within its paragraph. <see cref="IsRepeatedHeader"/>
/// marks a table header row Morph drew again at the top of a later page; its paragraph is the original's.
/// </summary>
public sealed record LayoutLine(BoundingBox Box, double Baseline, int ParagraphIndex, int LineIndex, IReadOnlyList<LayoutWord> Words, bool IsRepeatedHeader = false);

/// <summary>Which part of the document a paragraph comes from.</summary>
public enum LayoutStory
{
    Body,
    Header,
    Footer,
    Footnote,
    Endnote
}

/// <summary>
/// A paragraph's structure as the DOCX states it. <see cref="ListLabel"/> is the marker text Morph
/// drew ("1.", "a)", "•"); <see cref="Source"/> is the ordinal of the <c>w:p</c> across the document's
/// parts (main document first, then headers, footers, footnotes and endnotes); <see cref="Cell"/> is set for a paragraph in a
/// table cell. A header or footer paragraph appears once, with lines on every page that shows it.
/// <see cref="NoteId"/> is the <c>w:id</c> of the footnote or endnote a note paragraph belongs to.
/// <see cref="InTextBox"/> marks a paragraph inside a text box (<c>w:txbxContent</c>); its
/// <see cref="Story"/> is still the part the box is anchored in.
/// <see cref="OutlineLevel"/> is the <c>w:outlineLvl</c> the paragraph has, directly or through its style:
/// 0 for a top-level heading, null for body text.
/// </summary>
public sealed record LayoutParagraph(int Index, string? StyleId, string? ListLabel, int? ListLevel, int? Source, LayoutCellRef? Cell = null, LayoutStory Story = LayoutStory.Body, string? NoteId = null, bool InTextBox = false, int? OutlineLevel = null);

/// <summary>
/// A run of non-space characters as drawn, possibly spanning several runs. <see cref="Box"/> runs
/// from the font's ascender to its descender about the baseline the glyphs sit on.
/// <see cref="FontFamily"/> is the face actually drawn with, which differs from the requested family
/// when the host lacks it. <see cref="Rotation"/> is the clockwise angle of the baseline in degrees (90
/// for <c>btLr</c> cell text read bottom to top is -90); for a rotated word <see cref="Box"/> is the
/// axis-aligned box around it and <see cref="Baseline"/> the Y of its first glyph's baseline origin.
/// <see cref="FootnoteReferenceId"/> / <see cref="EndnoteReferenceId"/> are set on a note reference mark: the
/// <c>w:id</c> of the note it cites, matching <see cref="LayoutParagraph.NoteId"/> of that note's paragraphs.
/// </summary>
public sealed record LayoutWord(
    string Text,
    BoundingBox Box,
    double Baseline,
    string FontFamily,
    double FontSizePoints,
    bool Bold,
    bool Italic,
    bool IsListLabel,
    double Rotation = 0,
    string? FootnoteReferenceId = null,
    string? EndnoteReferenceId = null);
