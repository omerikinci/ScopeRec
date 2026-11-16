#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
if [ ! -x .venv-macapp/bin/python ]; then
  python3 -m venv .venv-macapp
fi
. .venv-macapp/bin/activate
python -m pip install -q -r mac_app/requirements.txt
exec python mac_app/scoperec_mac.py
