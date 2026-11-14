#!/usr/bin/env python3
from pathlib import Path
source = Path("src/Wafish.ReactUi/ClientApp/src/components/workspace.tsx")
lines = source.read_text().splitlines()
changed = [i for i, text in enumerate(lines, 1) if "const status = text(" in text or "field(apiError?.result, key) != null" in text]
lcov = Path("src/Wafish.ReactUi/ClientApp/coverage/lcov.info").read_text()
section = next(block for block in lcov.split("end_of_record") if "SF:src/components/workspace.tsx" in block)
hits = {int(line[3:].split(",")[0]): int(line.split(",")[1]) for line in section.splitlines() if line.startswith("DA:")}
for line in changed:
    print(f"{source}:{line}: {hits.get(line, 0)} executions")
assert len(changed) == 2 and all(hits.get(line, 0) > 0 for line in changed)
print("Changed frontend expressions: 2/2 covered (100%).")
