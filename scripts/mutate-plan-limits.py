"""Mutation pass over the plan limits and the readings drawn from them.

Three rules, all of which decide what a number on a screen means:

* `AllocationReading` — how full something is. Every mutation below produces a bar that renders
  perfectly and states something false: an unmeasured app drawn as idle, an unlimited one drawn as
  slightly full, a container over its ceiling drawn as comfortably inside it.
* `PlanOverage` — who a limit change is already biting. Lowering a limit takes nothing away from
  anybody, so this list is the only visible effect the change has; a mutation here makes the change
  look like it did nothing.
* `TemplateVersionEntry` — an operator putting a version into the dropdown by hand. The mutations
  produce rows that appear in the list and cannot be deployed, or that quietly take over as the
  default for every new deployment of that template.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
READING = ROOT / "src" / "Harbora.Infrastructure" / "Monitoring" / "AllocationReading.cs"
OVERAGE = ROOT / "src" / "Harbora.Infrastructure" / "Tenancy" / "PlanOverage.cs"
ENTRY = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "TemplateVersionEntry.cs"
SVCVER = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "ServiceVersions.cs"

FILTER = ("FullyQualifiedName~AllocationReadingTests|"
          "FullyQualifiedName~PlanOverageTests|"
          "FullyQualifiedName~TemplateVersionEntryTests|"
          "FullyQualifiedName~ServiceVersionsTests")

MUTANTS = [
    # --- how full something is ---
    (READING, "nothing measured is drawn as nothing used",
     'if (used is not { } measured || double.IsNaN(measured) || measured < 0)\n            return new AllocationReading(AllocationKind.Unmeasured, 0, false);',
     'var measured = used ?? 0;'),

    (READING, "an unlimited resource is given a share anyway",
     'if (allocated <= 0 || double.IsNaN(allocated))\n            return new AllocationReading(AllocationKind.Unlimited, 0, false);',
     'if (allocated < 0) return new AllocationReading(AllocationKind.Unlimited, 0, false);'),

    (READING, "over the ceiling is not reported as over",
     'return new AllocationReading(AllocationKind.Known, percent, measured > allocated);',
     'return new AllocationReading(AllocationKind.Known, percent, false);'),

    (READING, "the bar is allowed to overflow its track",
     'var percent = (int)Math.Round(Math.Clamp(share, 0, 100), MidpointRounding.AwayFromZero);',
     'var percent = (int)Math.Round(share, MidpointRounding.AwayFromZero);'),

    (READING, "a broken sample counts as a real one",
     '|| measured < 0)',
     '|| false)'),

    (READING, "an unlimited reading claims a share can be drawn",
     'public bool HasShare => Kind == AllocationKind.Known;',
     'public bool HasShare => Kind != AllocationKind.Unmeasured;'),

    # --- who a limit change is biting ---
    (OVERAGE, "memory over the plan goes unreported again",
     'if (usage.MaxMemoryBytes > 0 && usage.MemoryUsedBytes > usage.MaxMemoryBytes)\n            breaches.Add(new PlanBreach(PlanResource.Memory, usage.MemoryUsedBytes, usage.MaxMemoryBytes));',
     ''),

    (OVERAGE, "disk over the plan goes unreported",
     'if (usage.MaxDiskBytes > 0 && usage.DiskUsedBytes > usage.MaxDiskBytes)\n            breaches.Add(new PlanBreach(PlanResource.Disk, usage.DiskUsedBytes, usage.MaxDiskBytes));',
     ''),

    (OVERAGE, "sitting exactly on a limit counts as breaking it",
     'if (usage.MaxMemoryBytes > 0 && usage.MemoryUsedBytes > usage.MaxMemoryBytes)',
     'if (usage.MaxMemoryBytes > 0 && usage.MemoryUsedBytes >= usage.MaxMemoryBytes)'),

    (OVERAGE, "unlimited is treated as a limit of zero",
     'if (usage.MaxApps > 0 && usage.Apps > usage.MaxApps)',
     'if (usage.Apps > usage.MaxApps)'),

    (OVERAGE, "floating point noise reported as CPU overuse",
     'usage.CpuUsed > usage.MaxCpuCores + CpuTolerance',
     'usage.CpuUsed > usage.MaxCpuCores'),

    (OVERAGE, "the CPU tolerance widened into a hole",
     'private const double CpuTolerance = 1e-9;',
     'private const double CpuTolerance = 1.0;'),

    (OVERAGE, "only the first broken limit is reported",
     'return breaches;',
     'return breaches.Take(1).ToList();'),

    # --- adding a version by hand ---
    (ENTRY, "a tag no registry could have is passed through",
     'if (!ImageReference.IsUsableTag(trimmed)) return VersionEntryPlan.Refused(VersionEntryRefusal.InvalidTag);',
     ''),

    (ENTRY, "a duplicate is left to the unique index",
     'if (existingVersions.Any(v => string.Equals(v?.Trim(), trimmed, StringComparison.Ordinal)))\n            return VersionEntryPlan.Refused(VersionEntryRefusal.AlreadyExists);',
     ''),

    (ENTRY, "case folded away, so a real tag is refused as a duplicate of a different image",
     'string.Equals(v?.Trim(), trimmed, StringComparison.Ordinal)',
     'string.Equals(v?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)'),

    (ENTRY, "padding hides a duplicate",
     'string.Equals(v?.Trim(), trimmed, StringComparison.Ordinal)',
     'string.Equals(v, trimmed, StringComparison.Ordinal)'),

    (ENTRY, "a missing repository is not noticed until the lookup",
     'if (repo is null) return VersionEntryPlan.Refused(VersionEntryRefusal.UnknownRepository);',
     'repo ??= string.Empty;'),

    (ENTRY, "a registry port is taken for the tag of the repository",
     'var repo = ImageReference.RepositoryOf(repository);',
     'var repo = string.IsNullOrWhiteSpace(repository) ? null : repository.Split(\':\')[0];'),

    (ENTRY, "the added version arrives as a draft nobody sees",
     'Publication = VersionPublication.Published,',
     'Publication = VersionPublication.Draft,'),

    (ENTRY, "the added version takes over as the recommended one",
     'Lifecycle = VersionLifecycle.Stable,',
     'Lifecycle = VersionLifecycle.Recommended,'),

    (ENTRY, "a hand-typed version is passed off as a registry find",
     'DiscoveredAt = null',
     'DiscoveredAt = DateTimeOffset.UtcNow'),

    (ENTRY, "an empty architecture list is stored, making it undeployable everywhere",
     'basedOn?.SupportedArchitectures is { Length: > 0 } arch ? arch : "amd64"',
     'basedOn?.SupportedArchitectures ?? "amd64"'),

    (ENTRY, "the first version of a template loses its manifest",
     'ManifestJson = Retag(basedOn?.ManifestJson ?? templateManifestJson ?? "{}", plan.Repository, plan.Tag),',
     'ManifestJson = Retag(basedOn?.ManifestJson ?? "{}", plan.Repository, plan.Tag),'),

    (ENTRY, "the copied manifest keeps pointing at the version it came from",
     'obj["image"] = $"{repository}:{tag}";',
     ''),

    (ENTRY, "a refused plan is built anyway",
     'if (!plan.Allowed) throw new InvalidOperationException("A refused plan must not be built.");',
     ''),

    # --- which database versions are offered ---
    (SVCVER, "an emptied list leaves the dropdown with nothing in it",
     'return chosen.Count > 0 ? chosen : shipped;',
     'return chosen;'),

    (SVCVER, "the operator's list is merged with the shipped one instead of replacing it",
     'return chosen.Count > 0 ? chosen : shipped;',
     'return chosen.Count > 0 ? chosen.Concat(shipped).ToList() : shipped;'),

    (SVCVER, "the order typed is sorted away, changing which version is the default",
     'return chosen.Count > 0 ? chosen : shipped;',
     'return chosen.Count > 0 ? chosen.Order().ToList() : shipped;'),

    (SVCVER, "an entry that could not be a tag is offered anyway",
     'if (!ImageReference.IsUsableTag(piece)) continue;',
     ''),

    (SVCVER, "the same version is offered twice",
     'if (seen.Add(piece)) versions.Add(piece);',
     'versions.Add(piece);'),

    (SVCVER, "case folded away, so one legitimate tag swallows another",
     'new HashSet<string>(StringComparer.Ordinal)',
     'new HashSet<string>(StringComparer.OrdinalIgnoreCase)'),

    (SVCVER, "dropped entries are never reported to whoever typed them",
     '.Where(piece => !ImageReference.IsUsableTag(piece))',
     '.Where(piece => false)'),
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
