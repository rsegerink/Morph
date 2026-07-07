namespace Morph;

/// <summary>
/// Maps font family + style requests onto the bundled TrueType files so Sparkle embeds real
/// glyphs instead of falling back to whatever the host OS happens to have installed. Unlike
/// <c>PdfFontResolver</c> (PdfSharp), Sparkle has no process-global resolver hook — each
/// <see cref="SparkleRenderContext"/> owns its own resolver instance, scanned once for that
/// context's <c>FontDirectory</c>.
///
/// Font files follow the bundled naming convention <c>{Family}_{weight}[_Italic].ttf</c> (spaces in
/// the family become underscores), e.g. <c>Arial_Nova_700.ttf</c>, <c>Aptos_400_Italic.ttf</c>.
/// </summary>
sealed class SparkleFontResolver
{
    Dictionary<(string Family, bool Bold, bool Italic), string> index = [];
    string? defaultFace;

    public SparkleFontResolver(string? directory)
    {
        ScanDirectory(directory);
    }

    void ScanDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var full = System.IO.Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            return;
        }

        // Sort ordinally so indexing is filesystem-order independent: Directory.EnumerateFiles
        // has no defined order, yet that order decides the default fallback face (defaultFace is
        // the first file seen) and which file wins when two map to the same family/style key.
        // Without this the same FontDirectory embeds different fallback fonts on different
        // filesystems (e.g. a Windows bind mount vs CI's ext4), so the generated PDF bytes differ
        // across machines despite an identical container image.
        foreach (var path in Directory.EnumerateFiles(full, "*.ttf", SearchOption.AllDirectories)
                     .OrderBy(_ => _, StringComparer.Ordinal))
        {
            IndexFile(path);
        }
    }

    void IndexFile(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var italic = parts[^1].Equals("Italic", StringComparison.OrdinalIgnoreCase);
        var end = italic ? parts.Length - 1 : parts.Length;

        var weight = 400;
        if (end > 1 && int.TryParse(parts[end - 1], out var parsedWeight))
        {
            weight = parsedWeight;
            end--;
        }

        if (end <= 0)
        {
            return;
        }

        var family = string.Join(' ', parts[..end]);
        var bold = weight >= 600;

        // The bundled file name is the authoritative key and overrides any earlier entry.
        index[(family.ToLowerInvariant(), bold, italic)] = path;

        // Also index the font's own declared names (Family, Full, PostScript, Typographic
        // from the name table) so a request using an abbreviated or alternate spelling — e.g.
        // "Trade Gothic Next Cond" for Trade_Gothic_Next_Condensed — finds this exact file
        // instead of being suffix-stripped onto a different family ("Trade Gothic Next").
        // Mirrors PdfFontResolver's shared behavior. TryAdd keeps the file-name key (and the
        // first file in ordinal order) authoritative on collisions.
        foreach (var declaredName in ReadDeclaredNames(path))
        {
            index.TryAdd((declaredName.ToLowerInvariant(), bold, italic), path);
        }

        defaultFace ??= path;
    }

    static IEnumerable<string> ReadDeclaredNames(string path)
    {
        try
        {
            return OpenTypeReader.ReadFaces(path)
                .SelectMany(_ => _.Names)
                .Where(_ => !string.IsNullOrEmpty(_))
                .ToList();
        }
        catch
        {
            // A font we can't parse contributes no alternate names; the file-name index
            // entry still serves it.
            return [];
        }
    }

    /// <summary>
    /// Resolves the bundled font file for the given family/style, or <c>null</c> when nothing
    /// in this resolver's directory matches (the caller falls back to system fonts).
    /// </summary>
    public string? ResolvePath(string familyName, bool bold, bool italic)
    {
        // Order matters: keep the upright/italic axis correct before relaxing weight. When an
        // upright face is requested but only the italic of that exact weight is bundled (e.g.
        // Century Schoolbook ships 400-Italic, 700, 700-Italic but no 400 upright), falling back
        // to a different-weight upright reads far closer than swapping in a slanted same-weight
        // face. Mirrors PdfFontResolver.
        Span<(bool Bold, bool Italic)> attempts =
        [
            (bold, italic),
            (!bold, italic),
            (bold, !italic),
            (!bold, !italic)
        ];

        // Try the requested family, then its suffix-stripped base (e.g. "Bodoni MT Condensed"
        // -> "Bodoni MT"), mirroring the shared resolver's candidate-name fallback so a width-
        // or weight-suffixed request still finds the bundled base face instead of dropping to
        // the default sans fallback.
        var candidates = FontHelpers.GetCandidateNames(familyName, bold);
        foreach (var candidateName in FontFileCache.EnumerateCandidateNames(candidates))
        {
            var family = candidateName.ToLowerInvariant();
            foreach (var (attemptBold, attemptItalic) in attempts)
            {
                if (index.TryGetValue((family, attemptBold, attemptItalic), out var found))
                {
                    return found;
                }
            }
        }

        return defaultFace;
    }
}
