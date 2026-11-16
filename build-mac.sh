#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

dotnet_cmd="${DOTNET:-dotnet}"
if ! command -v "$dotnet_cmd" >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@8/bin/dotnet ]; then
  dotnet_cmd="/opt/homebrew/opt/dotnet@8/bin/dotnet"
fi

if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  echo "dotnet SDK bulunamadi. Homebrew ile kurun: brew install dotnet"
  exit 1
fi

rid="$(uname -m)"
case "$rid" in
  arm64) runtime="osx-arm64" ;;
  x86_64) runtime="osx-x64" ;;
  *) echo "Desteklenmeyen macOS mimarisi: $rid"; exit 1 ;;
esac

"$dotnet_cmd" publish ScopeCli.Mac.csproj -c Release -r "$runtime" --self-contained true -p:PublishSingleFile=true -o "dist/mac-$runtime"
echo "Hazir: dist/mac-$runtime/ScopeCli"
