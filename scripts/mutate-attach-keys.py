"""Mutation pass over which variables an attach writes.

A database handed an application a fixed set of names, which works exactly once. Attaching a second
PostgreSQL overwrote the first one's values under the same names: nothing failed at attach time,
nothing failed at deploy time, and the first query after the next release went to the wrong server.

Every mutation below either brings that failure back or breaks every application that exists today
by taking away the unprefixed names it already reads.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "AttachKeys.cs"

FILTER = "FullyQualifiedName~AttachKeysTests"

MUTANTS = [
    ("the second database takes the first one's names again",
     'var claimedByAnother = wanted.Any(w =>\n            existing.TryGetValue(w.Key, out var current) && current != w.Value);',
     'var claimedByAnother = false;'),

    ("no database ever gets the unprefixed names, breaking every existing application",
     'if (!claimedByAnother)\n            foreach (var (key, value) in wanted) final[key] = value;',
     ''),

    ("re-attaching loses the names this database already had",
     'existing.TryGetValue(w.Key, out var current) && current != w.Value);',
     'existing.ContainsKey(w.Key));'),

    ("a value that cannot be read is treated as this database's own",
     'existing.TryGetValue(w.Key, out var current) && current != w.Value);',
     'existing.TryGetValue(w.Key, out var current) && current is not null && current != w.Value);'),

    ("one claimed name out of two is not enough to fall back",
     'wanted.Any(w =>\n            existing.TryGetValue(w.Key, out var current) && current != w.Value);',
     'wanted.All(w =>\n            existing.TryGetValue(w.Key, out var current) && current != w.Value);'),

    ("nothing gets its own names, so the fallback has nowhere to go",
     'foreach (var (key, value) in wanted) final[prefix + key] = value;',
     ''),

    ("a name with nothing usable in it gets an empty prefix",
     'if (cleaned.Length == 0) cleaned = "SERVICE";',
     ''),

    ("a prefix may start with a digit, which a shell will not export",
     'return char.IsAsciiDigit(cleaned[0]) ? $"_{cleaned}_" : $"{cleaned}_";',
     'return $"{cleaned}_";'),

    ("runs of separators survive, so one name yields two prefixes",
     'while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");',
     ''),

    ("the prefix keeps its leading and trailing separators",
     ".Trim('_');",
     ";"),

    ("case is not normalised, so the prefix is not an environment variable name",
     '(serviceName ?? string.Empty).Trim().ToUpperInvariant()',
     '(serviceName ?? string.Empty).Trim()'),

    ("the report of falling back always says no",
     'wanted.Any(w => existing.TryGetValue(w.Key, out var current) && current != w.Value);',
     'false;'),
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
