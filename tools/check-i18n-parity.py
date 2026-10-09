#!/usr/bin/env python3
"""Falha quando o conjunto de chaves de um locale diverge do baseline en-US.

Uso: python3 tools/check-i18n-parity.py
Exit 0 = todos os locales em paridade; 1 = drift detectado.
"""
import glob
import json
import os
import sys

I18N_DIR = os.path.join(os.path.dirname(__file__), "..",
                        "src/OpenWebUI.Client/wwwroot/i18n")
BASELINE = "en-US.json"


def main() -> int:
    base_path = os.path.join(I18N_DIR, BASELINE)
    baseline = set(json.load(open(base_path, encoding="utf-8")))
    drift = False
    for path in sorted(glob.glob(os.path.join(I18N_DIR, "*.json"))):
        name = os.path.basename(path)
        if name in (BASELINE, "locales.json"):
            continue
        keys = set(json.load(open(path, encoding="utf-8")))
        missing = baseline - keys
        extra = keys - baseline
        if missing or extra:
            drift = True
            for k in sorted(missing):
                print(f"MISSING {name}: {k}")
            for k in sorted(extra):
                print(f"EXTRA   {name}: {k}")
    if drift:
        return 1
    print(f"OK: {len(baseline)} keys em paridade em todos os locales")
    return 0


if __name__ == "__main__":
    sys.exit(main())
