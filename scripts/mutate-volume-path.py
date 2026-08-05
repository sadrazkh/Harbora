"""Mutation pass over the volume file browser.

Two rules, and between them they are the only thing standing between a text box on a web page and
the filesystem of the machine the platform runs on.

* `VolumePath` decides whether a path is inside the volume. Every mutation either lets one out or
  refuses a legitimate filename, and only the second kind is visible without somebody trying.
* `VolumeFileCommands` builds the shell commands and reads what they print. The safety property is
  that every script is a constant and every path travels as a positional argument — a filename is
  attacker-controlled input, and an interpolated script is a shell waiting for one with a quote in
  it. The parser's mutations produce entries with invented names, sizes or dates in somebody's file
  list, which they then click.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "VolumePath.cs"
CMD = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "VolumeFileCommands.cs"

FILTER = "FullyQualifiedName~VolumePathTests|FullyQualifiedName~VolumeFileCommandTests"

ORDER_CLAUSE = (
    "        return entries\n"
    "            .OrderByDescending(e => e.IsDirectory)\n"
    "            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)\n"
    "            .ToList();"
)

MUTANTS = [
    # --- path confinement ---
    (RULE, "climbing out is resolved instead of refused",
     'if (segment == "..") return null;',
     'if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }'),

    (RULE, "climbing out is not checked at all",
     'if (segment == "..") return null;',
     ''),

    (RULE, "the check is on the raw string, so a dotfile is refused",
     "foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))",
     'if (path.Contains("..")) return null;\n        '
     "foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))"),

    (RULE, "a NUL byte survives to the kernel",
     "if (path.Contains('\\0')) return null;",
     ''),

    (RULE, "a backslash is translated rather than refused",
     "if (path.Contains('\\\\')) return null;",
     "path = path.Replace('\\\\', '/');"),

    (RULE, "a whitespace-only segment is accepted",
     'if (segment.Trim().Length == 0) return null;',
     ''),

    (RULE, "length is unbounded",
     'if (path.Length > MaxLength) return null;',
     ''),

    (RULE, "null input is treated as the root",
     'if (path is null) return null;',
     'path ??= string.Empty;'),

    (RULE, "the root gets a leading slash it never had",
     'return normalised.Length == 0 ? root : $"{root}/{normalised}";',
     'return $"{root}/{normalised}";'),

    (RULE, "the parent of the root points above it",
     'if (normalised.Length == 0) return null;',
     ''),

    # --- the commands, and the parser for what they print ---
    (CMD, "the path is interpolated into the script, so a filename becomes a shell",
     '["sh", "-c", ReadScript, "sh", absoluteFile];',
     '["sh", "-c", ReadScript + " # " + absoluteFile, "sh", absoluteFile];'),

    (CMD, "file contents are interpolated into the script",
     '["sh", "-c", WriteScript, "sh", absoluteFile, base64Content];',
     '["sh", "-c", WriteScript + " # " + base64Content, "sh", absoluteFile, base64Content];'),

    (CMD, "argv zero is dropped, shifting every argument by one",
     '["sh", "-c", ReadScript, "sh", absoluteFile];',
     '["sh", "-c", ReadScript, absoluteFile];'),

    (CMD, "a filename containing the separator is truncated",
     "Split('|', 4)",
     "Split('|')"),

    (CMD, "a malformed line becomes an entry with an invented name",
     'if (parts.Length != 4) continue;',
     'if (parts.Length < 1) continue;'),

    (CMD, "an unknown entry type is listed as a file",
     'if (!isDirectory && parts[0] != "f") continue;',
     ''),

    (CMD, "an unreadable size becomes zero",
     'if (!long.TryParse(parts[1], out var size) || size < 0) continue;',
     'long.TryParse(parts[1], out var size);'),

    (CMD, "an unreadable timestamp becomes 1970",
     'long.TryParse(parts[2], out var epoch) && epoch > 0',
     'long.TryParse(parts[2], out var epoch) || true'),

    (CMD, "a directory reports its own inode size as its contents",
     'isDirectory ? 0 : size,',
     'size,'),

    (CMD, "the order is whatever the shell happened to print",
     ORDER_CLAUSE,
     '        return entries;'),

    (CMD, "docker's framing bytes are left on the type field, so every folder reads as empty",
     'line.TrimStart(FrameBytes)',
     'line'),

    (CMD, "an empty name is listed",
     'if (parts[3].Length == 0) continue;',
     ''),
]

originals = {p: p.read_text(encoding="utf-8") for p in {RULE, CMD}}
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
