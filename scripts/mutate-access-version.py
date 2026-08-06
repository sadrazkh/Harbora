"""Mutation pass over the two rules phase 11 added.

AccessList decides who may reach a protected route; every mutant either widens the door or shuts it
on its owner without saying so. PanelVersion decides whether to urge an operator to upgrade; its
mutants announce updates that are not updates, or miss the one that is.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
ACCESS = ROOT / "src" / "Harbora.Infrastructure" / "Proxy" / "AccessList.cs"
VERSION = ROOT / "src" / "Harbora.Infrastructure" / "Maintenance" / "PanelVersion.cs"

FILTER = "FullyQualifiedName~AccessListTests|FullyQualifiedName~PanelVersionTests"

MUTANTS = [
    (ACCESS, "a bad entry is swallowed instead of reported",
     "else bad.Add(entry);",
     "else { }"),

    (ACCESS, "an invalid entry is passed through to Traefik",
     "if (IsValid(entry))\n            {\n                if (!accepted.Contains(entry, StringComparer.OrdinalIgnoreCase)) accepted.Add(entry);\n            }\n            else bad.Add(entry);",
     "accepted.Add(entry);"),

    (ACCESS, "the prefix length is never bounded",
     "return bits >= 0 && bits <= max;",
     "return bits >= 0;"),

    (ACCESS, "IPv6 gets IPv4's ceiling",
     "var max = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;",
     "var max = 32;"),

    (ACCESS, "duplicates survive",
     "if (!accepted.Contains(entry, StringComparer.OrdinalIgnoreCase)) accepted.Add(entry);",
     "accepted.Add(entry);"),

    (ACCESS, "a bare address is accepted without parsing",
     "if (slash < 0) return IPAddress.TryParse(entry, out _);",
     "if (slash < 0) return true;"),

    (VERSION, "a pre-release is offered as an upgrade",
     "if (theirs.PreRelease) return false;",
     ""),

    (VERSION, "an equal version counts as newer",
     "return theirs.CompareTo(mine) > 0;",
     "return theirs.CompareTo(mine) >= 0;"),

    (VERSION, "minor is compared before major",
     "if (Major != other.Major) return Major.CompareTo(other.Major);\n            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);",
     "if (Minor != other.Minor) return Minor.CompareTo(other.Minor);\n            if (Major != other.Major) return Major.CompareTo(other.Major);"),

    (VERSION, "versions compare as text, so 0.10 is older than 0.9",
     "if (Major != other.Major) return Major.CompareTo(other.Major);",
     "if (Major != other.Major) return string.CompareOrdinal(Major.ToString(), other.Major.ToString());"),

    (VERSION, "a four-part tag is accepted",
     "if (parts.Length is < 1 or > 3) return false;",
     "if (parts.Length < 1) return false;"),

    (VERSION, "an unparseable running version announces every tag",
     "if (!TryParse(current, out var mine)) return false;",
     "TryParse(current, out var mine);"),
]

originals = {p: p.read_text(encoding="utf-8") for p in {ACCESS, VERSION}}
survivors = []

for path, name, old, new in MUTANTS:
    original = originals[path]
    if old not in original:
        print(f"SKIP  {name}: pattern not found in {path.name}")
        survivors.append(name + " (pattern not found)")
        continue

    path.write_text(original.replace(old, new, 1), encoding="utf-8")
    time.sleep(1.1)
    result = subprocess.run(
        ["dotnet", "test", "tests/Harbora.Tests/Harbora.Tests.csproj", "--nologo", "-v", "q",
         "--filter", FILTER],
        cwd=ROOT, capture_output=True, text=True)

    caught = result.returncode != 0
    print(("CAUGHT" if caught else "SURVIVED") + f"  {name}")
    if not caught:
        survivors.append(name)

    path.write_text(original, encoding="utf-8")
    time.sleep(1.1)

subprocess.run(["dotnet", "build", "Harbora.slnx", "--nologo", "-v", "q"], cwd=ROOT, check=True)

print()
print(f"{len(MUTANTS) - len(survivors)}/{len(MUTANTS)} caught")
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
