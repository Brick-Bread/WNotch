"""Ticks PARITY.md lines that start with the given text (after "- [ ] " and any [changed] note).

    python3 rust/tools/tick-parity.py "Ordering: higher tier wins" "Snapshot order:"

Each text must match exactly one unticked line; anything else is reported and nothing is written.
"""

import re
import sys
from pathlib import Path

path = Path(__file__).resolve().parent.parent / "PARITY.md"
lines = path.read_text(encoding="utf-8").split("\n")
problems = []
for text in sys.argv[1:]:
    matches = []
    for i, line in enumerate(lines):
        if not line.startswith("- [ ] "):
            continue
        body = re.sub(r"^\*\*\[changed:.*?\]\*\* ", "", line[6:])
        if body.startswith(text):
            matches.append(i)
    if len(matches) != 1:
        problems.append(f"{len(matches)} matches: {text}")
    else:
        lines[matches[0]] = "- [x] " + lines[matches[0]][6:]

if problems:
    print("\n".join(problems))
    sys.exit(1)
path.write_text("\n".join(lines), encoding="utf-8")
print(f"ticked {len(sys.argv) - 1}")
