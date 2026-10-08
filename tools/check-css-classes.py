#!/usr/bin/env python3
"""Drift guard: fail if a class used in .razor markup has no rule in the
generated Tailwind CSS (E13 / SPEC-20261008-fix-undefined-css-classes).

Usage: tools/check-css-classes.py
Exit 0 when every used class is defined (utility or component layer),
exit 1 listing the used-but-undefined classes otherwise.
"""

from __future__ import annotations

import glob
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CLIENT = ROOT / "src" / "OpenWebUI.Client"
GENERATED_CSS = CLIENT / "wwwroot" / "css" / "tailwind.css"

# Class tokens that are emitted at runtime by JS/CSS rather than being
# authored utilities (kept small on purpose).
ALLOWLIST = {
    "dark",  # <html class="dark"> theme marker, toggled by JS
}

CLASS_ATTR = re.compile(r'class\s*=\s*"([^"@]*)"|class\s*=\s*\'([^\']*)\'')
TOKEN_OK = re.compile(r"^[A-Za-z0-9_\-./%\[\]()#@!,]+$")
SELECTOR_CLASS = re.compile(r"\.(\\.|[^\\.\s,{>+~:*#()\[\]])+")


def used_tokens() -> set[str]:
    tokens: set[str] = set()
    for path in glob.glob(str(CLIENT / "**" / "*.razor"), recursive=True):
        text = Path(path).read_text(encoding="utf-8")
        for match in CLASS_ATTR.finditer(text):
            for token in (match.group(1) or match.group(2) or "").split():
                if TOKEN_OK.match(token) and not token.startswith("@"):
                    tokens.add(token)
    return tokens


def defined_tokens(css: str) -> set[str]:
    """Class names that resolve to a selector in the generated CSS."""
    defined: set[str] = set()
    for match in SELECTOR_CLASS.finditer(css):
        name = re.sub(r"\\(.)", r"\1", match.group(0))[1:]
        # Strip leading variant prefixes (dark:, sm:, hover:, ...)
        base = name.split(":")[-1].lstrip("!")
        if base:
            defined.add(base)
    return defined


def main() -> int:
    if not GENERATED_CSS.exists():
        print(f"error: {GENERATED_CSS} not found — run the Tailwind build first")
        return 2

    defined = defined_tokens(GENERATED_CSS.read_text(encoding="utf-8"))
    missing = sorted(
        token.split(":")[-1].lstrip("!") for token in used_tokens() - ALLOWLIST
        if token.split(":")[-1].lstrip("!") not in defined
    )

    if missing:
        print("Classes used in .razor but not defined in generated tailwind.css:")
        for name in missing:
            print(f"  - {name}")
        return 1

    print("OK: every class used in .razor resolves to a rule in tailwind.css")
    return 0


if __name__ == "__main__":
    sys.exit(main())
