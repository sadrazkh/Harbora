"""Mutation pass over where a volume may be mounted.

Volumes could only arrive from a template, so nobody had ever typed one of these paths. Now they
can, and the dangerous answers look ordinary: an empty volume over /etc replaces the image's
configuration with nothing and the container stops resolving DNS; over / it does not start at all.

Every mutation below either lets one of those through, or refuses an ordinary path — and only the
second kind is visible without somebody trying it.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "MountPath.cs"

FILTER = "FullyQualifiedName~MountPathTests"

BACKSLASH_GUARD_NAME = "a backslash is accepted as part of a path"
BACKSLASH_GUARD = next(
    line.strip() for line in RULE.read_text(encoding="utf-8").splitlines()
    if "Contains" in line and "MountPathRefusal.Unsafe" in line)
# The same line with the second clause dropped, built by splitting rather than by writing the
# character out — which is what corrupted this file three times.
BACKSLASH_GUARD_WITHOUT = BACKSLASH_GUARD.split(" || ")[0] + ") return MountPathRefusal.Unsafe;"

MUTANTS = [
    ("a reserved directory is only matched exactly, so /usr/bin gets through",
     '''            if (value == reserved ||
                (reserved != "/" && value.StartsWith(reserved + "/", StringComparison.Ordinal)))''',
     '            if (value == reserved)'),

    ("the separator is dropped from the prefix check, so /etcetera is refused",
     'value.StartsWith(reserved + "/", StringComparison.Ordinal)',
     'value.StartsWith(reserved, StringComparison.Ordinal)'),

    ("nothing is reserved at all",
     '''        foreach (var reserved in Reserved)
            if (value == reserved ||
                (reserved != "/" && value.StartsWith(reserved + "/", StringComparison.Ordinal)))
                return MountPathRefusal.Reserved;''',
     ''),

    ("a relative path is accepted",
     "if (!value.StartsWith('/')) return MountPathRefusal.NotAbsolute;",
     ''),

    ("dot segments survive, so a path can walk into a reserved directory",
     '''        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment == ".." || segment == ".") return MountPathRefusal.Unsafe;''',
     ''),

    (BACKSLASH_GUARD_NAME, BACKSLASH_GUARD, BACKSLASH_GUARD_WITHOUT),

    ("length is unbounded",
     'if (value.Length > MaxLength) return MountPathRefusal.TooLong;',
     ''),

    ("a trailing separator makes a second mount of the same place",
     "var value = path.Trim().TrimEnd('/');",
     "var value = path.Trim();"),

    ("two applications mounting the same path share one volume",
     'return $"harbora-vol-{appSlug}-{tail}";',
     'return $"harbora-vol-{tail}";'),

    ("the volume name ignores the path, so two mounts collide",
     'return $"harbora-vol-{appSlug}-{tail}";',
     'return $"harbora-vol-{appSlug}";'),

    ("runs of separators survive into the volume name",
     'while (tail.Contains("--")) tail = tail.Replace("--", "-");',
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
