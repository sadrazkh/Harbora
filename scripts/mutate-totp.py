"""Mutation pass over the second factor.

The RFC vectors pin the arithmetic; these mutants aim at everything around it — the window, the
input hygiene, the recovery-code lifecycle. Each survivor is either a door that opens too easily
or one that locks its owner out.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Security" / "Totp.cs"

FILTER = "FullyQualifiedName~TotpTests"

MUTANTS = [
    ("the window doubles and old codes live twice as long",
     "private const long Window = 1;",
     "private const long Window = 2;"),

    ("no grace at all, and every slow phone is locked out",
     "private const long Window = 1;",
     "private const long Window = 0;"),

    ("a seven-digit paste verifies against its first six",
     "if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit)) return false;",
     "trimmed = new string(trimmed.Where(char.IsAsciiDigit).Take(Digits).ToArray());\n        if (trimmed.Length != Digits) return false;"),

    ("dynamic truncation reads a fixed offset",
     "var at = hash[^1] & 0x0F;",
     "var at = 0;"),

    ("the high bit survives and some codes go negative",
     "var binary = ((hash[at] & 0x7F) << 24)",
     "var binary = (hash[at] << 24)"),

    ("codes shrink to the last five digits",
     'return (binary % 1_000_000).ToString("D6");',
     'return (binary % 100_000).ToString("D6");'),

    ("a recovery code survives being spent",
     "return hashes.Remove(hash)\n            ? (true, System.Text.Json.JsonSerializer.Serialize(hashes))\n            : (false, storedJson);",
     "return hashes.Contains(hash)\n            ? (true, storedJson)\n            : (false, storedJson);"),

    ("recovery codes are stored in the clear",
     "public static string StoreRecoveryCodes(IEnumerable<string> codes) =>\n        System.Text.Json.JsonSerializer.Serialize(codes.Select(HashRecoveryCode));",
     "public static string StoreRecoveryCodes(IEnumerable<string> codes) =>\n        System.Text.Json.JsonSerializer.Serialize(codes);"),

    ("recovery matching is case-exact and the paper copy fails",
     "Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant())));",
     "Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));"),

    ("the secret shrinks to 40 bits",
     "public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));",
     "public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(5));"),
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
