"""Mutation pass over the bucket object browser.

The same shape as `mutate-volume-path.py`, because the danger is the same shape. A volume path that
climbs out reaches the host filesystem; an object key that climbs out reaches another prefix, and on
shared object storage the prefix next door belongs to another tenant.

* `ObjectKey` decides whether a key from a form is one the browser may act on. Every mutation here
  either lets a key out of the bucket or refuses a legitimate one — and only the second kind is
  visible without somebody trying.
* `BucketObjectCommands` builds the `mc` commands and reads what they print. The safety property is
  that every script is a constant and every key travels as a positional argument. The parser's
  mutations produce rows with invented names, sizes or dates in somebody's object list.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
KEY = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "ObjectKey.cs"
CMD = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "BucketObjectCommands.cs"

FILTER = "FullyQualifiedName~ObjectKeyTests|FullyQualifiedName~BucketObjectCommandTests"

ORDER_CLAUSE = (
    "        return entries\n"
    "            .OrderByDescending(e => e.IsFolder)\n"
    "            .ThenBy(e => e.Key, StringComparer.Ordinal)\n"
    "            .ToList();"
)

MUTANTS = [
    # --- which keys may be acted on ---
    (KEY, "climbing out is resolved instead of refused",
     'if (segment == "..") return null;',
     'if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }'),

    (KEY, "climbing out is not checked at all",
     'if (segment == "..") return null;',
     ''),

    (KEY, "the check is on the raw string, so a key with two dots in a name is refused",
     "foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))",
     'if (key.Contains("..")) return null;\n        '
     "foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))"),

    (KEY, "a backslash is translated rather than refused, addressing a different object",
     "if (key.Contains('\\\\')) return null;",
     "key = key.Replace('\\\\', '/');"),

    (KEY, "a control character reaches the listing",
     'if (key.Any(char.IsControl)) return null;',
     ''),

    (KEY, "a whitespace-only segment is accepted",
     'if (segment.Trim().Length == 0) return null;',
     ''),

    (KEY, "length is unbounded",
     'if (key.Length > MaxLength) return null;',
     ''),

    (KEY, "the ceiling is off by one",
     'if (key.Length > MaxLength) return null;',
     'if (key.Length >= MaxLength) return null;'),

    (KEY, "null is treated as the bucket root",
     'if (key is null) return null;',
     'key ??= string.Empty;'),

    (KEY, "the root counts as naming an object",
     'public static bool IsUsableObject(string? key) => Normalise(key) is { Length: > 0 };',
     'public static bool IsUsableObject(string? key) => Normalise(key) is not null;'),

    (KEY, "a refused prefix still offers somewhere to climb to",
     'if (string.IsNullOrEmpty(normalised)) return null;',
     'if (normalised is null) return string.Empty;\n        if (normalised.Length == 0) return null;'),

    (KEY, "the parent of a top-level folder is itself, so up one level goes nowhere",
     'return slash < 0 ? string.Empty : normalised[..slash];',
     'return normalised[..(slash < 0 ? normalised.Length : slash)];'),

    # --- the commands, and the parser for what they print ---
    (CMD, "the key is interpolated into the script, so an object name becomes a shell",
     '["sh", "-c", ReadScript, "sh", endpoint, accessKey, secretKey, bucket, key];',
     '["sh", "-c", ReadScript + " # " + key, "sh", endpoint, accessKey, secretKey, bucket, key];'),

    (CMD, "the uploaded bytes are interpolated into the script",
     '["sh", "-c", WriteScript, "sh", endpoint, accessKey, secretKey, bucket, key, base64];',
     '["sh", "-c", WriteScript + " # " + base64, "sh", endpoint, accessKey, secretKey, bucket, key, base64];'),

    (CMD, "the secret key is baked into the script text",
     '["sh", "-c", ListScript, "sh", endpoint, accessKey, secretKey, bucket, prefix];',
     '["sh", "-c", ListScript + " # " + secretKey, "sh", endpoint, accessKey, secretKey, bucket, prefix];'),

    (CMD, "argv zero is dropped, shifting every argument by one",
     '["sh", "-c", ReadScript, "sh", endpoint, accessKey, secretKey, bucket, key];',
     '["sh", "-c", ReadScript, endpoint, accessKey, secretKey, bucket, key];'),

    (CMD, "an entry with no key is listed under an empty name",
     'if (string.IsNullOrEmpty(key)) continue;',
     'key ??= "";'),

    (CMD, "a file with no size reported is shown as empty",
     'if (node["size"] is not { } sizeNode) continue;',
     'if (node["size"] is not { } sizeNode) { entries.Add(new BucketObject(key, false, 0, null)); continue; }'),

    (CMD, "a size that is not a number becomes zero",
     'catch (Exception e) when (e is FormatException or InvalidOperationException) { continue; }',
     'catch (Exception e) when (e is FormatException or InvalidOperationException) { size = 0; }'),

    (CMD, "a negative size is shown as it came",
     'if (size < 0) continue;',
     ''),

    (CMD, "an unreadable timestamp becomes 1970",
     'DateTimeOffset? modified = null;',
     'DateTimeOffset? modified = DateTimeOffset.UnixEpoch;'),

    (CMD, "a folder is required to carry a size, so every folder vanishes",
     "if (!isFolder)\n            {",
     "if (true)\n            {"),

    (CMD, "a trailing slash no longer means a folder",
     "var isFolder = key.EndsWith('/')\n                           || string.Equals(node[\"type\"]?.GetValue<string>(), \"folder\", StringComparison.Ordinal);",
     'var isFolder = string.Equals(node["type"]?.GetValue<string>(), "folder", StringComparison.Ordinal);'),

    (CMD, "docker's framing bytes hide every line",
     'var start = line.IndexOf(\'{\');\n            if (start < 0) continue;',
     'var start = 0;'),

    (CMD, "a line that is not json takes the whole listing down",
     'catch (System.Text.Json.JsonException) { continue; }',
     'catch (System.Text.Json.JsonException) { throw; }'),

    (CMD, "the order is whatever mc happened to print",
     ORDER_CLAUSE,
     '        return entries;'),

    (CMD, "folders sort in among the files",
     '            .OrderByDescending(e => e.IsFolder)\n',
     ''),
]

originals = {p: p.read_text(encoding="utf-8") for p in {KEY, CMD}}
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
