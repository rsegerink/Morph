#!/usr/bin/env bash
# Packs Morph.Layout (with this fork's Morph and Morph.Pdf inside) to the local NuGet feed as
# <Version>-dev.<timestamp>, and prints that version. Clean first: a Release build MSBuild thinks is up to
# date would be packed as is, under the new version.
#
# Runs dotnet from outside the repo, so global.json's .NET 11 preview pin does not apply (see FORK.md).
# MORPH_TEST_CONTAINER=1 leaves SponsorCheck out. ProjectDefaults turns GeneratePackageOnBuild on, which
# would also pack Morph and Morph.Pdf under their nuget.org ids; it is turned off for every project here.
set -euo pipefail

FEED="${FEED:-/mnt/data/Source/nuget_repo}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$(dirname "$PROJECT_DIR")"
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$SRC_DIR/Directory.Build.props")-dev.$(date +%Y%m%d%H%M%S)"

rm -rf "$SRC_DIR"/Morph/{bin,obj}/Release "$SRC_DIR"/Morph.Pdf/{bin,obj}/Release "$PROJECT_DIR"/{bin,obj}/Release

cd "$FEED"
MORPH_TEST_CONTAINER=1 dotnet pack "$PROJECT_DIR/Morph.Layout.csproj" -c Release -o "$FEED" -p:Version="$VERSION" -p:GeneratePackageOnBuild=false >&2

test -f "$FEED/Morph.Layout.$VERSION.nupkg"
echo "$VERSION"
