"""Mutation pass over path confinement inside a volume.

This rule is the only thing between a text box on a web page and the filesystem of the machine the
platform runs on. Every mutation below compiles, leaves the file browser working, and either lets a
path reach outside the volume or refuses a legitimate filename — and the first kind is not visible
until somebody uses it.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "VolumePath.cs"

FILTER = "FullyQualifiedName~VolumePathTests"

MUTANTS = [
    ("climbing out is resolved instead of refused",
     'if (segment == "..") return null;',
     'if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }'),

    ("climbing out is not checked at all",
     'if (segment == "..") return null;',
     ''),

    ("the check is on the raw string, so a dotfile is refused",
     'foreach (var segment in path.Split(\'/\', StringSplitOptions.RemoveEmptyEntries))',
     'if (path.Contains("..")) return null;\n        foreach (var segment in path.Split(\'/\', StringSplitOptions.RemoveEmptyEntries))'),

    ("a NUL byte survives to the kernel",
     'if (path.Contains(\'\\0\')) return null;',
     ''),

    ("a backslash is translated rather than refused",
     'if (path.Contains(\'\\\\\')) return null;',
     'path = path.Replace(\'\\\\\', \'/\');'),

    ("a whitespace-only segment is accepted",
     'if (segment.Trim().Length == 0) return null;',
     ''),

    ("length is unbounded",
     'if (path.Length > MaxLength) return null;',
     ''),

    ("null input is treated as the root",
     'if (path is null) return null;',
     'path ??= string.Empty;'),

    ("the root gets a leading slash it never had",
     'return normalised.Length == 0 ? root : $"{root}/{normalised}";',
     'return $"{root}/{normalised}";'),

    ("the parent of the root points above it",
     'if (normalised.Length == 0) return null;',
     ''),
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
