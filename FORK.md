# rsegerink/Morph fork

This fork tracks [Papyrine/Morph](https://github.com/Papyrine/Morph) for BladPluizer. Our own changes never go upstream.

```
origin   = https://github.com/rsegerink/Morph.git   (our fork)
upstream = https://github.com/Papyrine/Morph.git    (fetch only; push URL disabled)
```

Our additions live in their own projects (planned: `src/Morph.Layout`) plus at most one
`InternalsVisibleTo` line in `src/Morph/Morph.csproj`, so upstream merges stay conflict-free.

## Syncing with upstream

```
git fetch upstream --tags
git merge <upstream tag, e.g. 1.23.3>
git push origin <branch>
```

## Building

- `global.json` pins a .NET 11 preview SDK, but `Morph` and `Morph.Pdf` target `net10.0`.
  Running `dotnet build <path>/Morph.Pdf.csproj` from a directory outside the repo builds them with SDK 10.
- Release builds pull in SponsorCheck, which needs a GitHub token. `MORPH_TEST_CONTAINER=1`
  (upstream's own switch in `Morph.csproj`) leaves it out.

## History

Before 1.23.3 this fork carried `Morph.PDFSparkle`, a PDF backend on Sparkle.Net, because upstream
`Morph.Pdf` used PdfSharp. Upstream now renders PDF through Krilla (MIT), so PDFSparkle was dropped.
The old state is kept as tag `archive/pdfsparkle`.
