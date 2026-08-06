"""Mutation pass over the password-reset rules.

Every mutant is an account-takeover path: a link that outlives its window, one that works twice,
a token stored in a recognisable form. A survivor here is not a coverage statistic.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Security" / "PasswordReset.cs"

FILTER = "FullyQualifiedName~PasswordResetTests"

MUTANTS = [
    ("a used token works again",
     "if (row.UsedAt is not null) return PasswordResetRefusal.AlreadyUsed;",
     ""),

    ("expiry is inclusive, one extra moment of validity",
     "if (now >= row.ExpiresAt) return PasswordResetRefusal.Expired;",
     "if (now > row.ExpiresAt) return PasswordResetRefusal.Expired;"),

    ("expiry is never checked",
     "if (now >= row.ExpiresAt) return PasswordResetRefusal.Expired;",
     ""),

    ("a missing row redeems",
     "if (row is null) return PasswordResetRefusal.Unknown;",
     "if (row is null) return null;"),

    ("expired outranks used, telling the person the wrong story",
     "if (row.UsedAt is not null) return PasswordResetRefusal.AlreadyUsed;\n        if (now >= row.ExpiresAt) return PasswordResetRefusal.Expired;",
     "if (now >= row.ExpiresAt) return PasswordResetRefusal.Expired;\n        if (row.UsedAt is not null) return PasswordResetRefusal.AlreadyUsed;"),

    ("the token is stored as itself",
     "return (token, HashOf(token));",
     "return (token, token);"),

    ("the token shrinks to a guessable size",
     "var bytes = RandomNumberGenerator.GetBytes(32);",
     "var bytes = RandomNumberGenerator.GetBytes(4);"),

    ("the token keeps URL-hostile characters",
     ".Replace('+', '-').Replace('/', '_').TrimEnd('=')",
     ""),
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
