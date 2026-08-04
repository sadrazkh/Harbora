"""Mutation pass over registry discovery.

The failure mode this guards against is a job that logs a clean run and either does nothing or fills
the catalogue with things that are not releases. Both look identical in the log.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
TAG = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "RegistryTag.cs"
DISCOVERY = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "RegistryDiscovery.cs"
SERVICE = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "RegistryDiscoveryService.cs"
REFERENCE = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "RegistryReference.cs"
DI = ROOT / "src" / "Harbora.Infrastructure" / "DependencyInjection.cs"

FILTER = ("FullyQualifiedName~RegistryDiscoveryTests|"
          "FullyQualifiedName~RegistryDiscoveryServiceTests|"
          "FullyQualifiedName~NavigationMapTests")

MUTANTS = [
    (TAG, "release candidates become versions",
     'if (PreReleaseMarkers.Any(m => variant.StartsWith(m, StringComparison.OrdinalIgnoreCase)))\n                return null;',
     ''),

    (TAG, "a commit hash parses as a version",
     'if (piece.Length == 0 || !piece.All(char.IsAsciiDigit)) return null;',
     'if (piece.Length == 0) return null;'),

    (TAG, "versions compare as text",
     'if (mine != theirs) return mine.CompareTo(theirs);',
     'if (mine != theirs) return mine.ToString().CompareTo(theirs.ToString());'),

    (TAG, "shape ignores how many parts a tag has",
     'a.Parts.Count == b.Parts.Count\n        && string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase);',
     'string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase);'),

    (TAG, "shape ignores the variant",
     'a.Parts.Count == b.Parts.Count\n        && string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase);',
     'a.Parts.Count == b.Parts.Count;'),

    (DISCOVERY, "older releases are offered as new options",
     '.Where(t => t.Parsed!.CompareTo(newest.Parsed!) > 0)',
     '.Where(t => t.Parsed!.CompareTo(newest.Parsed!) != 0)'),

    (DISCOVERY, "the shape already in use is ignored",
     '.Where(t => RegistryTag.SameShape(t.Parsed!, newest.Parsed!))',
     '.Where(t => true)'),

    (DISCOVERY, "a run adds everything it finds",
     '.Take(Math.Max(0, maximum))',
     '.Take(int.MaxValue)'),

    (DISCOVERY, "an unreadable catalogue pulls in every tag",
     'if (known.Count == 0) return [];',
     'if (known.Count == 0 && false) return [];'),

    (DISCOVERY, "results are oldest first",
     '.OrderByDescending(t => t.Parsed!)',
     '.OrderBy(t => t.Parsed!)'),

    (DISCOVERY, "a discovered version arrives published",
     'Publication = VersionPublication.Draft,',
     'Publication = VersionPublication.Published,'),

    (DISCOVERY, "a discovered version claims to be the recommended one",
     'Lifecycle = VersionLifecycle.Stable,',
     'Lifecycle = VersionLifecycle.Recommended,'),

    (DISCOVERY, "a discovered version keeps the image it was copied from",
     'ManifestJson = Retag(basedOn.ManifestJson, basedOn.ImageRepository, tag),',
     'ManifestJson = basedOn.ManifestJson,'),

    (SERVICE, "the setting is ignored and registries are called anyway",
     'if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))\n            return 0;',
     ''),

    (SERVICE, "anything truthy-looking turns discovery on",
     'if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))\n            return 0;',
     'if (string.IsNullOrEmpty(enabled))\n            return 0;'),

    (SERVICE, "a tag with no digest is stored anyway",
     'if (digest is null) continue;',
     'digest ??= "";'),

    (REFERENCE, "any host is called",
     'if (!Allowed.TryGetValue(firstSegment, out var host)) return null;',
     'var host = Allowed.TryGetValue(firstSegment, out var known) ? known : firstSegment;'),

    (REFERENCE, "official images lose their library prefix",
     'var path = value.Contains(\'/\') ? value : $"library/{value}";',
     'var path = value;'),

    (DI, "the job is registered only as a hosted service",
     'services.AddSingleton<Templates.RegistryDiscoveryService>();\n        services.AddHostedService(sp => sp.GetRequiredService<Templates.RegistryDiscoveryService>());',
     'services.AddHostedService<Templates.RegistryDiscoveryService>();'),
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
