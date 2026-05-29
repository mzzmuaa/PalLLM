"""
One-shot sweep for Pass 376: rewrite references to docs deleted in
this pass (AGENT_NATIVE, REPLICATION_KIT, EASY_MODE, FIRST_HOUR,
AGENTIC_PATTERNS_2026, MEMORY_RECIPES, COMPANION_INTELLIGENCE) so
the audit's dangling-link check stays green.

Replacement strategy per doc:
- AGENT_NATIVE       -> ../AGENTS.md (its content was a duplicate)
- REPLICATION_KIT    -> CODE_MAP.md (the canonical "where things live")
- EASY_MODE          -> QUICKSTART.md (the canonical first-chat path)
- FIRST_HOUR         -> QUICKSTART.md
- AGENTIC_PATTERNS_2026, MEMORY_RECIPES, COMPANION_INTELLIGENCE
  -> markdown link removed; the surrounding prose stays.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

REPLACEMENTS: list[tuple[str, str]] = [
    # Markdown link forms first (more specific). Replace the link, not the prose.
    (r"\[`?AGENT_NATIVE\.md`?\]\(([^)]+)\)", "[`AGENTS.md`](../AGENTS.md)"),
    (r"\[`?REPLICATION_KIT\.md`?\]\(([^)]+)\)", "[`CODE_MAP.md`](CODE_MAP.md)"),
    (r"\[`?EASY_MODE\.md`?\]\(([^)]+)\)", "[`QUICKSTART.md`](QUICKSTART.md)"),
    (r"\[`?FIRST_HOUR\.md`?\]\(([^)]+)\)", "[`QUICKSTART.md`](QUICKSTART.md)"),
    # The three retired docs — replace the link with bare backticked filename so
    # the prose still references the concept without dangling.
    (r"\[`?AGENTIC_PATTERNS_2026\.md`?\]\(([^)]+)\)",
     "AGENTIC_PATTERNS_2026 (retired Pass 418)"),
    (r"\[`?MEMORY_RECIPES\.md`?\]\(([^)]+)\)",
     "MEMORY_RECIPES (retired Pass 418)"),
    (r"\[`?COMPANION_INTELLIGENCE\.md`?\]\(([^)]+)\)",
     "COMPANION_INTELLIGENCE (retired Pass 418)"),
    # Backticked bare-path mentions — the path-reference audit treats these
    # as candidates and fails on a missing file. Strip the backticks and the
    # extension so the prose still names the concept.
    (r"`docs/AGENT_NATIVE\.md`", "AGENT_NATIVE (retired Pass 376; see `AGENTS.md`)"),
    (r"`docs/REPLICATION_KIT\.md`", "REPLICATION_KIT (retired Pass 376; see `docs/CODE_MAP.md`)"),
    (r"`docs/EASY_MODE\.md`", "EASY_MODE (retired Pass 376; see `docs/QUICKSTART.md`)"),
    (r"`docs/FIRST_HOUR\.md`", "FIRST_HOUR (retired Pass 376; see `docs/QUICKSTART.md`)"),
    (r"`docs/AGENTIC_PATTERNS_2026\.md`", "AGENTIC_PATTERNS_2026 (retired Pass 418)"),
    (r"`docs/MEMORY_RECIPES\.md`", "MEMORY_RECIPES (retired Pass 418)"),
    (r"`docs/COMPANION_INTELLIGENCE\.md`", "COMPANION_INTELLIGENCE (retired Pass 418)"),
    # Bare path mentions (no markdown link, no backticks)
    (r"\bdocs/AGENT_NATIVE\.md\b", "AGENTS.md"),
    (r"\bAGENT_NATIVE\.md\b", "AGENTS.md"),
    (r"\bdocs/REPLICATION_KIT\.md\b", "docs/CODE_MAP.md"),
    (r"\bREPLICATION_KIT\.md\b", "CODE_MAP.md"),
    (r"\bdocs/EASY_MODE\.md\b", "docs/QUICKSTART.md"),
    (r"\bEASY_MODE\.md\b", "QUICKSTART.md"),
    (r"\bdocs/FIRST_HOUR\.md\b", "docs/QUICKSTART.md"),
    (r"\bFIRST_HOUR\.md\b", "QUICKSTART.md"),
]

TARGETS = [
    "docs/COMPLETION.md",
    "docs/FUTURE_2035.md",
    "docs/HANDOFF.md",
    "docs/IMPLEMENTATION_QUEUE.md",
    "docs/LOCAL_MODELS_INVENTORY.md",
    "docs/MODELS_2026.md",
    "docs/MULTIMODAL_RECIPES.md",
    "docs/READINESS.md",
    "docs/ROADMAP.md",
    "docs/UX_PRINCIPLES.md",
    "docs/HARVEST.md",
    "docs/QUICKREF.md",
    "docs/INDEX.md",
    "docs/CHEAT_SHEET.md",
    "docs/AGENTS.md",
    "AGENTS.md",
    "docs/CONTRIBUTING.md",
    "CONTRIBUTING.md",
    # Historical narrative — backticked-path references in old pass entries
    # also fail the path-reference audit (it doesn't distinguish historical
    # mentions). The replacement is non-destructive: it keeps the prose,
    # just trades the path for "(retired Pass 418)".
    "CHANGELOG.md",
]


def main() -> int:
    total = 0
    for rel in TARGETS:
        path = REPO / rel
        if not path.is_file():
            continue
        text = path.read_text(encoding="utf-8")
        original = text
        count = 0
        for pattern, replacement in REPLACEMENTS:
            text, n = re.subn(pattern, replacement, text)
            count += n
        if text != original:
            path.write_text(text, encoding="utf-8")
            print(f"{rel:50s} {count:3d} replacements")
            total += count
    print(f"\nTotal: {total} replacements across {len(TARGETS)} target files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
