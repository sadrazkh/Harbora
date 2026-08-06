"""Mutation pass over the relative-age rule.

Small rule, but its failures are the plausible kind: a threshold nudged and "47 hours ago" becomes
"1 day ago" early, a ceiling instead of a floor and every age is one unit older than it is, the
skew clamp dropped and a backup taken by a fast clock reads as scheduled for the future.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Web" / "Infrastructure" / "Dates.cs"

FILTER = "FullyQualifiedName~DateFormattingTests"

MUTANTS = [
    ("minutes hand over to hours a minute early",
     "if (span.TotalMinutes < 60)",
     "if (span.TotalMinutes < 59)"),

    ("hours never hand over to days",
     "if (span.TotalHours < 48)",
     "if (span.TotalHours < 4800)"),

    ("ages round up instead of flooring",
     "var h = (int)span.TotalHours;",
     "var h = (int)Math.Ceiling(span.TotalHours);"),

    ("days round up instead of flooring",
     "var d = (int)span.TotalDays;",
     "var d = (int)Math.Ceiling(span.TotalDays);"),

    ("the just-now window swallows the first hour",
     "if (span.TotalMinutes < 1)",
     "if (span.TotalMinutes < 60)"),
]

original = RULE.read_text(encoding="utf-8")
survivors = []

for name, old, new in MUTANTS:
    if old not in original:
        print(f"SKIP  {name}: pattern not found")
        survivors.append(name + " (pattern not found)")
        continue

    RULE.write_text(original.replace(old, new, 1), encoding="utf-8")
    time.sleep(1.1)
    result = subprocess.run(
        ["dotnet", "test", "tests/Harbora.Tests/Harbora.Tests.csproj", "--nologo", "-v", "q",
         "--filter", FILTER],
        cwd=ROOT, capture_output=True, text=True)

    caught = result.returncode != 0
    print(("CAUGHT" if caught else "SURVIVED") + f"  {name}")
    if not caught:
        survivors.append(name)

    RULE.write_text(original, encoding="utf-8")
    time.sleep(1.1)

subprocess.run(["dotnet", "build", "Harbora.slnx", "--nologo", "-v", "q"], cwd=ROOT, check=True)

print()
print(f"{len(MUTANTS) - len(survivors)}/{len(MUTANTS)} caught")
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
