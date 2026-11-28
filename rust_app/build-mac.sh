#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
CARGO_BIN="${RUST_CARGO:-$HOME/.cargo/bin/cargo}"
if [ ! -x "$CARGO_BIN" ]; then CARGO_BIN="$(command -v cargo)"; fi
"$CARGO_BIN" build --manifest-path rust_app/Cargo.toml --release --bins --locked
APP="$PWD/dist/ScopeRec Rust.app"
TASK_STAGE="$(mktemp -d /tmp/scoperec-rust-build.XXXXXX)"
trap 'rm -rf "$TASK_STAGE"' EXIT
mkdir -p "$TASK_STAGE/ScopeRec Rust.app/Contents/MacOS" "$TASK_STAGE/ScopeRec Rust.app/Contents/Resources" "$TASK_STAGE/ScopeRecRust.iconset"
for executable in scoperec-rust scoperec-rust-cli scoperec-simulator; do
  cp "rust_app/target/release/$executable" "$TASK_STAGE/ScopeRec Rust.app/Contents/MacOS/$executable"
done
cp rust_app/packaging/Info.plist "$TASK_STAGE/ScopeRec Rust.app/Contents/Info.plist"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" logo/scoperec-kare.png --out "$TASK_STAGE/ScopeRecRust.iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" logo/scoperec-kare.png --out "$TASK_STAGE/ScopeRecRust.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$TASK_STAGE/ScopeRecRust.iconset" -o "$TASK_STAGE/ScopeRec Rust.app/Contents/Resources/ScopeRecRust.icns"
codesign --force --deep --sign - "$TASK_STAGE/ScopeRec Rust.app"
codesign --verify --deep --strict "$TASK_STAGE/ScopeRec Rust.app"
mkdir -p dist
if [ -e "$APP" ]; then mv "$APP" "$TASK_STAGE/previous.app"; fi
mv "$TASK_STAGE/ScopeRec Rust.app" "$APP"
if [ "${1:-}" = "--install" ]; then
  if pgrep -f '/Applications/ScopeRec Rust.app/Contents/MacOS/scoperec-rust' >/dev/null; then
    echo 'Close ScopeRec Rust before updating it.' >&2
    exit 1
  fi
  if [ -e '/Applications/ScopeRec Rust.app' ]; then
    mv '/Applications/ScopeRec Rust.app' "$TASK_STAGE/previous-installed.app"
  fi
  ditto "$APP" '/Applications/ScopeRec Rust.app'
  codesign --verify --deep --strict '/Applications/ScopeRec Rust.app'
fi
echo "Built: $APP"
