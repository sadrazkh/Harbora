"""Mutation pass over S3 bucket naming.

The rules are not ours and cannot be relaxed: a name the storage server rejects fails at
provisioning time, after the row has been written and somebody has been told they have a bucket.
Every mutation below either lets through a name a real server refuses, or refuses one it would have
accepted — and only the second kind is visible without somebody trying.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "BucketName.cs"

FILTER = "FullyQualifiedName~BucketNameTests"

MUTANTS = [
    ("uppercase is silently lowercased instead of refused",
     "(c >= 'a' && c <= 'z')",
     "char.IsAsciiLetter(c)"),

    ("periods are allowed, so the bucket cannot be reached over TLS",
     "|| c == '-')",
     "|| c == '-' || c == '.')"),

    ("the character check is skipped entirely",
     '''        foreach (var c in name)
            if (!(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'z') || c == '-'))
                return BucketNameRefusal.BadCharacters;''',
     ''),

    ("a name may start or end with a hyphen",
     'if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))\n            return BucketNameRefusal.BadEnds;',
     ''),

    ("only the first character is checked, so a trailing hyphen survives",
     '!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1])',
     '!char.IsAsciiLetterOrDigit(name[0])'),

    ("something shaped like an address is accepted",
     'if (LooksLikeAnAddress(name)) return BucketNameRefusal.LooksLikeAnAddress;',
     ''),

    ("the address check is too wide and refuses ordinary names",
     'return parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));',
     'return parts.All(p => p.Length > 0);'),

    ("reserved suffixes are accepted",
     '''        foreach (var suffix in ReservedSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return BucketNameRefusal.ReservedSuffix;''',
     ''),

    ("the minimum length is not enforced",
     'if (name.Length < MinLength) return BucketNameRefusal.TooShort;',
     ''),

    ("the maximum length is not enforced",
     'if (name.Length > MaxLength) return BucketNameRefusal.TooLong;',
     ''),

    ("a name is trimmed, so two names become one",
     'if (string.IsNullOrWhiteSpace(name)) return BucketNameRefusal.Missing;',
     'if (string.IsNullOrWhiteSpace(name)) return BucketNameRefusal.Missing;\n        name = name.Trim();'),

    ("the suggestion may be something the form would reject",
     'return IsValid(cleaned) ? cleaned : null;',
     'return cleaned;'),

    ("the suggestion keeps its separators at the ends",
     "cleaned = cleaned.Trim('-');",
     ''),

    ("the suggestion is not shortened to the limit",
     'if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].Trim(\'-\');',
     ''),

    ("runs of separators survive into the suggestion",
     'while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");',
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
