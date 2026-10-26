#!/usr/bin/env bash
# Packs Morph.Layout (with this fork's Morph and Morph.Pdf inside) as <Version>-dev.<timestamp>, pushes it
# to our GitHub Packages feed and prints that version. The push uses the "github" source with the token
# from your own NuGet config (see the BladPluizer README, "NuGet-feed"). Clean first: a Release build
# MSBuild thinks is up to date would be packed as is, under the new version.
#
# Runs dotnet from outside the repo, so global.json's .NET 11 preview pin does not apply (see FORK.md).
# MORPH_TEST_CONTAINER=1 leaves SponsorCheck out. ProjectDefaults turns GeneratePackageOnBuild on, which
# would also pack Morph and Morph.Pdf under their nuget.org ids; it is turned off for every project here.
set -euo pipefail

SOURCE="${SOURCE:-github}"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$(dirname "$PROJECT_DIR")"
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$SRC_DIR/Directory.Build.props")-dev.$(date +%Y%m%d%H%M%S)"

rm -rf "$SRC_DIR"/Morph/{bin,obj}/Release "$SRC_DIR"/Morph.Pdf/{bin,obj}/Release "$PROJECT_DIR"/{bin,obj}/Release

cd "$OUT"
MORPH_TEST_CONTAINER=1 dotnet pack "$PROJECT_DIR/Morph.Layout.csproj" -c Release -o "$OUT" -p:Version="$VERSION" -p:GeneratePackageOnBuild=false >&2
dotnet nuget push "$OUT/Morph.Layout.$VERSION.nupkg" --source "$SOURCE" --no-symbols >&2
echo "$VERSION"
