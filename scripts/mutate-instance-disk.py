"""Mutation pass over storage in a resource tier.

A tier was CPU and memory only, so every picker offered "1 vCPU / 1 GB" and said nothing about the
figure people actually run out of. Three rules came out of adding it, and each mutation below
compiles and leaves a screen that looks right:

* `InstanceSizeLabel` — a tier that claims storage it does not have, or hides storage it does.
* `InstanceDisk` — a resize refused for data nobody measured, or accepted onto a tier too small to
  hold what is already stored, which is discovered at the next write.
* `InstanceSizeKey` — a key that works everywhere except the one place it is split on or matched in,
  where a tier silently reads as "no limit".

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
LABEL = ROOT / "src" / "Harbora.Infrastructure" / "Tenancy" / "InstanceSizeLabel.cs"
DISK = ROOT / "src" / "Harbora.Infrastructure" / "Tenancy" / "InstanceDisk.cs"
KEY = ROOT / "src" / "Harbora.Infrastructure" / "Tenancy" / "InstanceSizeKey.cs"
BYTES = ROOT / "src" / "Harbora.Infrastructure" / "Tenancy" / "ByteSize.cs"

FILTER = ("FullyQualifiedName~InstanceSizeDiskTests|"
          "FullyQualifiedName~InstanceSizeKeyTests|"
          "FullyQualifiedName~DiskQuotaTests")

MUTANTS = [
    # --- how a tier reads ---
    (LABEL, "a tier with no storage claims unlimited storage",
     'return diskBytes > 0 ? $"{label} / {ByteSize.Format(diskBytes)}" : label;',
     'return $"{label} / {ByteSize.Format(diskBytes)}";'),

    (LABEL, "storage is dropped from the label entirely",
     'return diskBytes > 0 ? $"{label} / {ByteSize.Format(diskBytes)}" : label;',
     'return label;'),

    (LABEL, "a fractional core is rounded into a whole one",
     '{cpuCores:0.##} vCPU',
     '{cpuCores:0} vCPU'),

    # --- what fits in it ---
    (DISK, "exactly full is refused, so a tier cannot hold what it advertises",
     'tierDiskBytes <= 0 || usage.MeasuredBytes <= tierDiskBytes;',
     'tierDiskBytes <= 0 || usage.MeasuredBytes < tierDiskBytes;'),

    (DISK, "a tier with no ceiling refuses everything",
     'tierDiskBytes <= 0 || usage.MeasuredBytes <= tierDiskBytes;',
     'usage.MeasuredBytes <= tierDiskBytes;'),

    (DISK, "the ceiling is ignored and everything fits",
     'tierDiskBytes <= 0 || usage.MeasuredBytes <= tierDiskBytes;',
     'true;'),

    (DISK, "unmeasured volumes are counted as full, refusing a resize on a guess",
     'tierDiskBytes <= 0 || usage.MeasuredBytes <= tierDiskBytes;',
     'tierDiskBytes <= 0 || (usage.UnmeasuredResources == 0 && usage.MeasuredBytes <= tierDiskBytes);'),

    (DISK, "the refusal stops naming the figures",
     '$"This tier comes with {ByteSize.Format(tierDiskBytes)} of disk and " +\n               $"{ByteSize.Format(usage.MeasuredBytes)} is already stored. " +\n               "Delete some data first, or choose a larger tier.";',
     '"That tier is too small.";'),

    (DISK, "something that fits is explained anyway",
     'if (Fits(tierDiskBytes, usage)) return null;',
     ''),

    (DISK, "an unmeasured volume is never mentioned",
     'usage.UnmeasuredResources == 0\n            ? null',
     'true\n            ? null'),

    # --- one way of writing bytes ---
    (BYTES, "an empty measurement is reported as unlimited",
     'public static string Measured(long bytes) => bytes <= 0 ? "0 B" : Format(bytes);',
     'public static string Measured(long bytes) => Format(bytes);'),

    (BYTES, "nothing is written as zero rather than as unlimited",
     '<= 0 => unlimited,',
     '<= 0 => "0 B",'),

    (BYTES, "gigabytes are reported as megabytes",
     '_ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"',
     '_ => $"{bytes / (1024.0 * 1024):0.#} MB"'),

    # --- the key a tier is known by ---
    (KEY, "a key that cannot be made is stored as empty, matching every unsized resource",
     'if (cleaned.Length == 0) return null;',
     ''),

    (KEY, "a comma survives and splits the plan's allow list in two",
     'char.IsAsciiLetterOrDigit(c) ? c : \'-\'',
     'c'),

    (KEY, "runs of separators are kept, so one name becomes two tiers",
     'while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");',
     ''),

    (KEY, "a key keeps its leading and trailing separators",
     '.Trim(\'-\');\n\n        // Runs',
     ';\n\n        // Runs'),

    (KEY, "case is kept, so one tier can be created twice",
     'key.Trim().ToLowerInvariant()',
     'key.Trim()'),

    (KEY, "an overlong key is stored whole",
     'return cleaned.Length > MaxLength ? cleaned[..MaxLength].Trim(\'-\') : cleaned;',
     'return cleaned;'),

    (KEY, "cutting an overlong key leaves a trailing separator",
     'cleaned[..MaxLength].Trim(\'-\')',
     'cleaned[..MaxLength]'),
]

originals = {path: path.read_text(encoding="utf-8") for path in {m[0] for m in MUTANTS}}
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
