"""Mutation pass over the orphaned-image rule.

Every mutant here deletes something that is not ours: a customer's image, a living app's rollback
window, the neighbour whose slug merely shares a prefix. The failures are one-way — a wrongly
deleted image is a rollback that no longer exists — so a survivor is not a style problem.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Maintenance" / "CleanupPlan.cs"

FILTER = "FullyQualifiedName~CleanupPlanTests"

MUTANTS = [
    ("the prefix check is a substring match",
     '.Where(t => t.StartsWith(prefix, StringComparison.Ordinal))',
     '.Where(t => t.Contains(prefix, StringComparison.Ordinal))'),

    ("the prefix loses its slash and claims the neighbour's registry",
     'var prefix = imagePrefix + "/";',
     'var prefix = imagePrefix;'),

    ("living apps protect nothing",
     '.Where(t => !BelongsToALivingApp(t, prefix, slugs))',
     ''),

    ("slug comparison folds case",
     'if (string.Equals(name, slug, StringComparison.Ordinal)) return true;',
     'if (string.Equals(name, slug, StringComparison.OrdinalIgnoreCase)) return true;'),

    ("the compose dash boundary is dropped",
     'if (name.StartsWith(slug + "-", StringComparison.Ordinal)) return true;',
     'if (name.StartsWith(slug, StringComparison.Ordinal)) return true;'),

    ("an unreadable tag is deletable",
     'if (name.Length == 0) return true;',
     'if (name.Length == 0) return false;'),

    ("blank slugs protect everything under the dash rule",
     'var slugs = liveSlugs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();',
     'var slugs = liveSlugs.ToList();'),

    ("duplicates are deleted twice",
     '.Distinct(StringComparer.Ordinal)\n            .ToList();',
     '.ToList();'),
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
