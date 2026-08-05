"""Mutation pass over folding the side panels away.

Both panels were always drawn, so the shelf of ready-made apps sat beside the list of apps somebody
had already made, on every visit, forever. Adding a preference brings the trap nullable booleans
always carry: "closed" and "never asked" are different answers, and every mutation below collapses
them in some direction — reopening a panel the person deliberately shut, or hiding one nobody chose
to hide.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Navigation" / "RailVisibility.cs"

FILTER = "FullyQualifiedName~RailVisibilityTests"

MUTANTS = [
    ("a deliberate 'closed' is read as no answer, so the panel reopens every visit",
     'if (userChoice is { } chosen) return chosen;',
     'if (userChoice == true) return true;'),

    ("the person's choice is ignored entirely",
     'if (userChoice is { } chosen) return chosen;',
     ''),

    ("the operator's default overrules the person's own choice",
     'if (userChoice is { } chosen) return chosen;\n\n        return platformDefault ?? ShippedDefault(panel);',
     'return platformDefault ?? userChoice ?? ShippedDefault(panel);'),

    ("an operator who set nothing silently closes everything",
     'return platformDefault ?? ShippedDefault(panel);',
     'return platformDefault ?? false;'),

    ("both panels get the same shipped answer",
     'RailPanel.QuickStart => false,\n        _ => true',
     '_ => true'),

    ("quick start is shipped open, back in the way it was",
     'RailPanel.QuickStart => false,',
     'RailPanel.QuickStart => true,'),

    ("overview is shipped closed, hiding the counts the page exists for",
     '_ => true\n    };',
     '_ => false\n    };'),

    ("a cleared setting reads as closed rather than as no answer",
     '_ => null\n    };',
     '_ => false\n    };'),

    ("anything that is not 'false' counts as true",
     '"true" => true,\n        "false" => false,',
     '"false" => false,\n        null => null,\n        _ when true => true,'),

    ("a stored setting is matched case-sensitively and stops being read",
     'stored?.Trim().ToLowerInvariant()',
     'stored'),

    ("clearing a choice stores 'false', which is a different answer",
     '_ => string.Empty',
     '_ => "false"'),
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
