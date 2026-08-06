"""Mutation pass over the per-application threshold rule.

The failures on either side are both real: a rule that fires on a passing spike fills a channel
with noise until somebody mutes it — and a muted channel reports nothing at all — while a rule that
never fires is a feature that reports success while doing nothing.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Monitoring" / "ThresholdRule.cs"

FILTER = "FullyQualifiedName~ThresholdRuleTests"

MUTANTS = [
    ("one sample anywhere in the window is enough",
     "if (window.Any(s => s.Percent!.Value < thresholdPercent)) return false;",
     "if (window.All(s => s.Percent!.Value < thresholdPercent)) return false;"),

    ("the window need not be covered, so one fresh sample sustains ten minutes",
     "if (sustain > TimeSpan.Zero && window[0].At > now - sustain + Tolerance) return false;",
     ""),

    ("a gap counts as a breach",
     "if (window.Any(s => s.Percent is null)) return false;",
     ""),

    ("no samples at all is a breach",
     "if (window.Count == 0) return false;",
     "if (window.Count == 0) return true;"),

    ("an unconfigured threshold fires on everything",
     "if (thresholdPercent <= 0) return false;",
     ""),

    ("exactly at the line does not count",
     "if (window.Any(s => s.Percent!.Value < thresholdPercent)) return false;",
     "if (window.Any(s => s.Percent!.Value <= thresholdPercent)) return false;"),

    ("future samples are judged",
     ".Where(s => s.At <= now && s.At >= now - sustain)",
     ".Where(s => s.At >= now - sustain)"),

    ("older samples outside the window are judged too",
     ".Where(s => s.At <= now && s.At >= now - sustain)",
     ".Where(s => s.At <= now)"),

    ("a standing breach repeats on every tick",
     "lastFiredAt is not { } last || now - last >= RepeatAfter;",
     "true;"),

    ("a breach never repeats once fired",
     "lastFiredAt is not { } last || now - last >= RepeatAfter;",
     "lastFiredAt is null;"),
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
