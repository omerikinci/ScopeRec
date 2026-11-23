#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
if [ ! -x .venv-macapp/bin/python ]; then
  python3 -m venv .venv-macapp
fi
. .venv-macapp/bin/activate
python -m pip install -q -r mac_app/requirements.txt pyinstaller pillow
extra=()
if [ -f /opt/homebrew/lib/libusb-1.0.dylib ]; then
  extra+=(--add-binary "/opt/homebrew/lib/libusb-1.0.dylib:.")
elif [ -f /usr/local/lib/libusb-1.0.dylib ]; then
  extra+=(--add-binary "/usr/local/lib/libusb-1.0.dylib:.")
fi
pyinstaller --noconfirm --windowed --name ScopeRec \
  --osx-bundle-identifier com.omerikinci.ScopeRec \
  --icon logo/scoperec-kare.png \
  --add-data "logo/scoperec-kare.png:logo" \
  "${extra[@]}" mac_app/scoperec_mac.py
echo "Hazir: dist/ScopeRec.app"
