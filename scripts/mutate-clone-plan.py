"""Mutation pass over the environment clone plan.

Copying an environment is almost entirely a naming problem, and every naming mistake here is the
same mistake: the copy reaches into the original. Two docker volumes with one name are one volume.
A container name that is already taken is somebody's running database. A connection variable carried
over verbatim points the copy at production, and everything looks like it worked.

So the mutants below are mostly "drop a uniqueness check" and "carry something over that should have
been rebuilt". Each one produces a copy that comes up and runs.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
PLAN = ROOT / "src" / "Harbora.Infrastructure" / "Projects" / "ClonePlan.cs"

FILTER = "FullyQualifiedName~ClonePlanTests|FullyQualifiedName~EnvironmentClonerTests"

MUTANTS = [
    # --- the environment ---
    ("an environment slug already in the project is reused",
     "var environmentSlug = Unique(\n            ProjectService.Slugify(request.DesiredName) is { Length: > 0 } s ? s : \"environment\",\n            request.TakenEnvironmentSlugs);",
     "var environmentSlug = ProjectService.Slugify(request.DesiredName) is { Length: > 0 } s ? s : \"environment\";"),

    ("a name with nothing usable in it produces an empty slug",
     'ProjectService.Slugify(request.DesiredName) is { Length: > 0 } s ? s : "environment"',
     'ProjectService.Slugify(request.DesiredName)'),

    ("the typed name is stored with its whitespace",
     'request.DesiredName.Trim()',
     'request.DesiredName'),

    ("the slug is shown as the name even when one was typed",
     'var name = string.IsNullOrWhiteSpace(request.DesiredName)\n            ? environmentSlug\n            : request.DesiredName.Trim();',
     'var name = environmentSlug;'),

    # --- applications ---
    ("an application slug already in the workspace is reused",
     'var slug = Unique(Suffixed(app.Slug, environmentSlug, "app"), appSlugs);',
     'var slug = Suffixed(app.Slug, environmentSlug, "app");'),

    ("the plan is unique against the database but not against itself",
     'appSlugs.Add(slug);',
     ''),

    ("an application keeps its original slug, so the copy collides with it",
     'var slug = Unique(Suffixed(app.Slug, environmentSlug, "app"), appSlugs);',
     'var slug = Unique(app.Slug, appSlugs);'),

    ("a volume is named after the original, so the copy writes into production's data",
     '$"harbora-vol-{slug}-{ProjectService.Slugify(v.MountPath.Trim(\'/\'))}"',
     '$"harbora-vol-{app.Slug}-{ProjectService.Slugify(v.MountPath.Trim(\'/\'))}"'),

    ("two volumes on one application collapse into one name",
     '$"harbora-vol-{slug}-{ProjectService.Slugify(v.MountPath.Trim(\'/\'))}"',
     '$"harbora-vol-{slug}"'),

    ("a volume loses the limits it was given",
     'v.MountPath, v.ReadOnly, v.SizeLimitBytes)).ToList()));',
     'v.MountPath, false, null)).ToList()));'),

    # --- databases ---
    ("a container name already in use is reused",
     'var slug = Unique(\n                Suffixed(ProjectService.Slugify(service.Name), environmentSlug, "service"), serviceSlugs);',
     'var slug = Suffixed(ProjectService.Slugify(service.Name), environmentSlug, "service");'),

    ("two databases in one plan race for one container name",
     'serviceSlugs.Add(slug);',
     ''),

    ("the taken container names are compared with their prefix still on",
     'request.TakenContainerNames.Select(StripContainerPrefix)',
     'request.TakenContainerNames'),

    ("the suffix that resolved the collision never reaches the database name",
     'service.HasDatabaseName ? slug.Replace(\'-\', \'_\') : string.Empty,',
     'service.HasDatabaseName ? ProjectService.Slugify(service.Name).Replace(\'-\', \'_\') : string.Empty,'),

    ("a database name keeps the hyphens no engine accepts",
     "slug.Replace('-', '_')",
     "slug"),

    ("an engine with no database gets one invented",
     'service.HasDatabaseName ? slug.Replace(\'-\', \'_\') : string.Empty,',
     'slug.Replace(\'-\', \'_\'),'),

    ("the data volume is named after the container of some other service",
     '$"{container}-data"',
     '"harbora-svc-data"'),

    # --- what the package costs and what it leaves behind ---
    ("the databases are left out of the memory the package asks for",
     'Apps.Sum(a => a.MemoryLimitBytes) + Services.Sum(s => s.MemoryLimitBytes);',
     'Apps.Sum(a => a.MemoryLimitBytes);'),

    ("the databases are left out of the cpu the package asks for",
     'Apps.Sum(a => a.CpuLimit) + Services.Sum(s => s.CpuLimit);',
     'Apps.Sum(a => a.CpuLimit);'),

    ("the databases are left out of the resource count",
     'public int ResourceCount => Apps.Count + Services.Count;',
     'public int ResourceCount => Apps.Count;'),

    ("the domains left behind are not counted, so the screen says none",
     'request.Apps.Sum(a => a.DomainCount));',
     '0);'),

    # --- the variables an attach owns ---
    ("only the prefixed name is treated as owned, so the shared one is carried over stale",
     'owned.Add(key);\n                owned.Add(prefix + key);',
     'owned.Add(prefix + key);'),

    ("only the shared name is treated as owned",
     'owned.Add(key);\n                owned.Add(prefix + key);',
     'owned.Add(key);'),

    ("an application is read as attached to a service whose prefix it merely contains",
     'appVariableKeys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))',
     'appVariableKeys.Any(k => k.Contains(prefix, StringComparison.Ordinal) || !k.Contains(\'_\'))'),

    ("every application is read as attached to every database",
     'return appVariableKeys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));',
     'return appVariableKeys.Any();'),

    ("no application is ever read as attached, so nothing is rewired",
     'return appVariableKeys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));',
     'return false;'),

    # --- the uniquifier itself ---
    ("the suffix search stops at the first taken candidate",
     'if (!taken.Contains(next, StringComparer.OrdinalIgnoreCase)) return next;',
     'return next;'),

    ("names collide when they differ only in case",
     'if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;',
     'if (!taken.Contains(candidate, StringComparer.Ordinal)) return candidate;'),

    # --- the cloner itself: what it carries over, and what it refuses ---
    ("the database password is carried over, so the copy reaches the original's database",
     'EncryptedPassword = protector.Protect(Harbora.Infrastructure.Services.ServiceCredentials.Generate()),',
     'EncryptedPassword = origin.EncryptedPassword,'),

    ("the copy inherits the protected flag that says this one is the real one",
     'IsDefault = false,\n            IsProtected = false,',
     'IsDefault = false,\n            IsProtected = true,'),

    ("the copy is made the project's default environment",
     'IsDefault = false,\n            IsProtected = false,',
     'IsDefault = true,\n            IsProtected = false,'),

    ("a copied database reports a size measured on the original's volume",
     'CpuLimit = spec.CpuLimit,',
     'CpuLimit = spec.CpuLimit,\n                StorageBytes = origin.StorageBytes,\n'
     '                StorageMeasuredAt = origin.StorageMeasuredAt,\n'
     '                RunningImage = origin.RunningImage,'),

    ("the copy inherits the original's preview habit and its history",
     'TemplateId = origin.TemplateId,',
     'TemplateId = origin.TemplateId,\n                PreviewsEnabled = origin.PreviewsEnabled,\n'
     '                ActiveDeploymentId = origin.ActiveDeploymentId,\n'
     '                PublishedHostPort = origin.PublishedHostPort,'),

    ("a copied volume claims the size measured on the original's data",
     'SizeLimitBytes = originVolume.SizeLimitBytes,',
     'SizeLimitBytes = originVolume.SizeLimitBytes,\n'
     '                    StorageBytes = originVolume.StorageBytes,\n'
     '                    StorageMeasuredAt = originVolume.StorageMeasuredAt,'),

    ("every variable is carried over, connection settings included",
     'if (owned.Contains(variable.Key)) continue;',
     ''),

    ("the copy's own configuration is dropped along with the connection settings",
     'if (owned.Contains(variable.Key)) continue;',
     'continue;'),

    ("the attach is detected from the copy, which by then proves nothing",
     'origin.EnvironmentVariables.Select(v => v.Key), s.Name))',
     'copy.EnvironmentVariables.Select(v => v.Key), s.Name))'),

    ("a copy is made anyway when the original's connection settings cannot be read",
     'return CloneOutcome.Refused(\n                    "The database connection settings of the original could not be read, so the copy was not made.",\n                    plan);',
     'attachKeys.Add((spec.Name, Array.Empty<string>()));'),

    ("the package is never weighed as a whole, only item by item",
     'if (usage.MaxApps > 0 && usage.Apps + plan.Apps.Count > usage.MaxApps)',
     'if (false)'),

    ("the database count is left out of the package check",
     'if (usage.MaxServices > 0 && usage.Services + plan.Services.Count > usage.MaxServices)',
     'if (false)'),

    ("a refusal from the ordinary quota check is swallowed",
     'if (!check.Allowed) return check.Reason;\n        }\n        foreach (var service in plan.Services)',
     'if (!check.Allowed) return null;\n        }\n        foreach (var service in plan.Services)'),

    ("an environment belonging to another workspace can be copied",
     '.FirstOrDefaultAsync(e => e.Id == sourceEnvironmentId && e.WorkspaceId == workspaceId, ct);\n        if (source is null) return null;',
     '.FirstOrDefaultAsync(e => e.Id == sourceEnvironmentId, ct);\n        if (source is null) return null;'),

    ("an environment with nothing in it is copied into an empty one",
     'if (plan.ResourceCount == 0)\n            return CloneOutcome.Refused("There is nothing in that environment to copy.", plan);',
     ''),

    ("a node with no room stops nothing",
     'if (!placed.Ok || placed.ServerId is not { } server)\n                return CloneOutcome.Refused(placed.Reason ?? "No server has capacity for this copy.", plan);\n            placements[app.SourceId] = server;',
     'placements[app.SourceId] = placed.ServerId ?? Guid.Empty;'),
]

SERVICE = ROOT / "src" / "Harbora.Infrastructure" / "Projects" / "EnvironmentCloner.cs"
MARKER = ROOT / "scripts" / ".mutation-in-progress"

# A run killed part-way leaves a mutant in the working tree, and the NEXT run reads that mutated
# file as its baseline -- so it restores to the mutant, and every mutant after it is measured
# against broken source. This happened once; the marker is what makes it impossible to happen
# quietly. It is written before the first edit and removed after the last restore.
if MARKER.exists():
    print("A previous run did not finish. Restore these files before running again:")
    print(f"  {PLAN}")
    print(f"  {SERVICE}")
    print(f"then delete {MARKER}.")
    sys.exit(2)

original = PLAN.read_text(encoding="utf-8")
service_original = SERVICE.read_text(encoding="utf-8")
MARKER.write_text("restore the two files named in mutate-clone-plan.py", encoding="utf-8")

survivors = []

only = sys.argv[1] if len(sys.argv) > 1 else None

for name, old, new in MUTANTS:
    if only and only not in name:
        continue
    target, source = (PLAN, original) if old in original else (SERVICE, service_original)
    if old not in source:
        print(f"SKIP  {name}: pattern not found")
        survivors.append(name + " (pattern not found)")
        continue

    target.write_text(source.replace(old, new, 1), encoding="utf-8")
    time.sleep(1.1)
    result = subprocess.run(
        ["dotnet", "test", "tests/Harbora.Tests/Harbora.Tests.csproj", "--nologo", "-v", "q",
         "--filter", FILTER],
        cwd=ROOT, capture_output=True, text=True)

    caught = result.returncode != 0
    print(("CAUGHT" if caught else "SURVIVED") + f"  {name}")
    if not caught:
        survivors.append(name)

    target.write_text(source, encoding="utf-8")
    time.sleep(1.1)

MARKER.unlink()
subprocess.run(["dotnet", "build", "Harbora.slnx", "--nologo", "-v", "q"], cwd=ROOT, check=True)

print()
ran = len([m for m in MUTANTS if not only or only in m[0]])
print(f"{ran - len(survivors)}/{ran} caught" + (f" (filtered on '{only}')" if only else ""))
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
