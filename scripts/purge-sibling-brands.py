"""
One-shot brand-purge utility for Pass 372 (PalLLM).

Goal: remove every mention of the sibling projects Byte, OmniForge,
DeepForge, and Vulcan so the repository can be made public without
leaking the maintainer's other (still-private) project names. The
replacements preserve every passage's substantive content; only the
sibling-attribution wording changes.

RimLLM intentionally stays — the operator's purge list did not include
it (it is referenced as "sibling research" elsewhere and is already
treated as ambient).

Run once:
    python scripts/purge-sibling-brands.py

The script is idempotent; running it twice produces no further diff.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# Files to touch — every file the earlier grep found, plus a handful
# of tooling/test guards that need to learn the new pattern.
TARGET_FILES = [
    "CHANGELOG.md",
    "docs/HANDOFF.md",
    "docs/INDEX.md",
    "docs/COMPANION_INTELLIGENCE.md",
    "docs/RESEARCH_NOTES_2026-05.md",
    "src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs",
]

# Ordered list of (regex, replacement). Earlier rules win; later rules
# only fire on whatever remains. Order matters when one substitution's
# output would feed into a later, narrower pattern.
REPLACEMENTS: list[tuple[str, str]] = [
    # --- 1. Local-path mentions ----------------------------------------
    # "D:\Coding\Byte\docs\prompts\byte-..." style references
    (
        r"`D:[\\/]+Coding[\\/]+(?:Byte|OmniForge|DeepForge|Vulcan)[^`\s]*`",
        "an external sibling-project tree",
    ),
    (
        r"`D:/Coding/Byte/docs/prompts/byte-\{[^}]+\}[^`]*`",
        "an external prompt-pack tree",
    ),
    # "D:\Coding\OmniForge\src\..." style code paths
    (
        r"D:[\\/]+Coding[\\/]+(?:Byte|OmniForge|DeepForge|Vulcan)[\w\\/.\-]*",
        "an external sibling tree",
    ),
    # --- 2. Multi-sibling "scan" rosters --------------------------------
    # "OmniForge, DeepForge, BYTE, and RimLLM"
    (
        r"\b(?:OmniForge|DeepForge|BYTE|Byte|Vulcan)(?:\s*[,/]\s*(?:OmniForge|DeepForge|BYTE|Byte|Vulcan|RimLLM))+(?:,?\s+and\s+(?:OmniForge|DeepForge|BYTE|Byte|Vulcan|RimLLM))?",
        "external sibling research",
    ),
    # "OmniForge and DeepForge"
    (
        r"\b(?:OmniForge|DeepForge|BYTE|Byte|Vulcan)\s+and\s+(?:OmniForge|DeepForge|BYTE|Byte|Vulcan)\b",
        "external sibling research",
    ),
    # --- 3. Possessive / single-mention forms ---------------------------
    # "Byte's prompt packs", "OmniForge's runtime", "DeepForge's pattern"
    (
        r"\b(?:Byte|OmniForge|DeepForge|Vulcan)'s\s+",
        "an external project's ",
    ),
    # "from Byte" / "from OmniForge" / etc.
    (
        r"\bfrom\s+(?:Byte|OmniForge|DeepForge|Vulcan)\b",
        "from external research",
    ),
    # "in Byte", "in OmniForge", etc.
    (
        r"\b(?:in|under|inside)\s+(?:Byte|OmniForge|DeepForge|Vulcan)\b",
        "in external sibling research",
    ),
    # Bare project names — last so the more-specific rules above
    # absorb the contextual cases first.
    (r"\bByte\s+prompt\s+pack(s|\b)", r"external prompt-pack research"),
    (r"\bByte\s+library\b", r"external research library"),
    (r"\bByte\s+audit\b", r"external prompt-pack audit"),
    (r"\bByte\s+source\s+patterns?\b", r"external source patterns"),
    (r"\bByte\s+telemetry\b", r"external telemetry research"),
    (r"\bByte\b", r"the external prompt-pack project"),
    (r"\bOmniForge\b", r"an external asset-generation sibling"),
    (r"\bDeepForge\b", r"an action-RPG sibling runtime"),
    (r"\bVulcan\b", r"an unrelated sibling project"),
    # --- 4. Pack identifiers (byte-forge, byte-forward, ...) ------------
    (
        r"\bbyte-(?:forge|forward|synthesis|qwen-frontier|qwen-modernize|council)\b",
        "external-pack",
    ),
    # --- 5. Filename / placeholder noise --------------------------------
    # `byte-{forge,forward,...}-*` literal placeholder
    (
        r"`byte-\{[^}]+\}[^`]*`",
        "external prompt-pack files",
    ),
]


def transform(text: str) -> tuple[str, int]:
    """Apply every replacement; return (new_text, total_substitutions)."""
    total = 0
    for pattern, replacement in REPLACEMENTS:
        new_text, count = re.subn(pattern, replacement, text)
        if count:
            total += count
            text = new_text
    return text, total


def main() -> int:
    parser = argparse.ArgumentParser(description="Purge sibling-project brands from tracked PalLLM files.")
    parser.add_argument("--dry-run", action="store_true", help="Report counts only; do not write.")
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parents[1]))
    args = parser.parse_args()

    repo = Path(args.repo_root)
    missing: list[str] = []
    summary: list[tuple[str, int]] = []
    for rel in TARGET_FILES:
        target = repo / rel
        if not target.is_file():
            missing.append(rel)
            continue
        original = target.read_text(encoding="utf-8")
        rewritten, count = transform(original)
        if count == 0:
            summary.append((rel, 0))
            continue
        if not args.dry_run:
            target.write_text(rewritten, encoding="utf-8")
        summary.append((rel, count))

    print(f"Purge sweep ({'dry-run' if args.dry_run else 'applied'}) under {repo}")
    for rel, count in summary:
        print(f"  {rel:55s} {count:5d} substitutions")
    if missing:
        print("Missing (skipped):", ", ".join(missing))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
