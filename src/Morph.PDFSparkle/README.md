# Morph.PDFSparkle

DOCX → PDF rendering backend for Morph, built on [Sparkle.Net](https://github.com/rsegerink/PDFSparkle.Net) (`Siberix.Sparkle.PDF`) instead of PdfSharp (which `Morph.Pdf` uses).

This directory is **our own code**, maintained on the [rsegerink/Morph](https://github.com/rsegerink/Morph) fork. The rest of the Morph repository is a fork of [Papyrine/Morph](https://github.com/Papyrine/Morph) and is not ours to modify beyond what's needed to support this backend (currently just one `InternalsVisibleTo` line in `Morph/Morph.csproj`).

## Public API

- `SparkleDocumentConverter.ConvertToPdf(docxPath, options)` / `ConvertToPdf(docxStream, options)` — one-shot DOCX → PDF, mirrors `Morph.Pdf.PdfDocumentConverter`.
- `WordDocument.ExportToSparklePdf(options)` — extension method for parse-once/export-many workflows.

Both take the shared `PdfExportOptions` (`FontDirectory`, `FontWidthScale`, `FontFallback`, `Pages`, `OnWarning`, ...).

## Architecture

Same split as every other Morph rendering backend:

- `SparkleRenderer` — top-level render entry point; also normalizes output for determinism (see below).
- `SparkleRenderContext` — per-render state: fonts, brushes/pens, images, all cached by value/reference.
- `SparklePageRenderer` — drives the shared `PageRendererBase` layout engine, supplies Sparkle drawing primitives.
- `SparkleTextEngine` — paragraph layout (line-breaking, tab stops, widow/orphan control, spacing) and text drawing.
- `SparkleFontResolver` — maps requested font family/style onto bundled TTF files in `ConversionOptions.FontDirectory`.

## Determinism

Sparkle output is normalized so identical input produces byte-identical PDFs across runs/machines — required for the Verify-based snapshot tests in `Tests/`:

- `Info.Created`/`Info.Modified` pinned to a fixed timestamp (Sparkle stamps `DateTime.Now` by default).
- Embedded composite-font subset tag prefixes (`/BaseFont /XXXXXX+Name`) are remapped to a deterministic sequence (`AAAAAA`, `AAAAAB`, ...) instead of Sparkle's random 6-letter prefix.
- Font resolution is restricted to `FontDirectory` (no system-font fallback) when a directory is configured, same as `Morph.Pdf`.

Two Sparkle.Net bugs were fixed upstream (in the `PDFSparkle.Net` repo, not here) to make this possible:
- `Utils/HashSet.cs` iterated in reference-hash order, making `/Font`, `/XObject`, `/ExtGState` and `/Pattern` resource dictionaries non-deterministic. Now insertion-ordered.
- `PDF/Image.cs` read `SKBitmap.Pixels` (which copies the whole pixel array) *inside* the per-pixel loop — O(width × height²) instead of O(width × height), effectively hanging on any non-trivial image. Fixed to read once outside the loop.

## Known limitations vs. Morph.Pdf

- **No page trimming.** `options.Pages` only early-exits the render loop (skips laying out pages past the requested range) — it can't remove already-created pages from the output, because Sparkle.Net's public `IPageCollection` has no `Remove`/`RemoveAt`. `Morph.Pdf` can trim via `PdfDocument.Pages.RemoveAt`.
- **Blank trailing page isn't removed from the PDF itself**, only from the internal page counter, for the same reason.
- No HTML → PDF converter (out of scope — DOCX → PDF only, per project decision).

## Working with the fork

`git status`/`git log` in this repo operate on the `rsegerink/Morph` fork, not `Papyrine/Morph`:

```
origin  = https://github.com/Papyrine/Morph.git   (upstream, read-only for us)
fork    = https://github.com/rsegerink/Morph.git  (our fork; local main tracks this)
```

To pull in upstream Papyrine/Morph changes while keeping the `Morph.PDFSparkle` commit(s) on top:

```
git fetch origin
git rebase origin/main
git push fork main --force-with-lease
```
