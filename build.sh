#!/usr/bin/env bash
# Builds the plugin in a .NET 10 SDK container (no local .NET install needed)
# and packages it as dist/cinephage-requests_<version>.zip (files at the zip root,
# as Jellyfin's plugin repository installer expects).
set -euo pipefail

cd "$(dirname "$0")"
VERSION="$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' Jellyfin.Plugin.Cinephage/Jellyfin.Plugin.Cinephage.csproj)"
ZIP="dist/cinephage-requests_${VERSION}.zip"

rm -rf dist
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish Jellyfin.Plugin.Cinephage/Jellyfin.Plugin.Cinephage.csproj -c Release -o /src/dist/publish

mkdir -p dist/package
cp dist/publish/Jellyfin.Plugin.Cinephage.dll dist/package/
sed "s/__VERSION__/${VERSION}/; s/__TIMESTAMP__/$(date -u +%Y-%m-%dT%H:%M:%SZ)/" meta.json > dist/package/meta.json
(cd dist/package && zip -qX "../$(basename "$ZIP")" ./*)

echo "Klaar: $ZIP"
echo "Versie:   $VERSION"
echo "Checksum: $(md5sum "$ZIP" | cut -d' ' -f1)"
