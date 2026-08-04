"""Mutation pass over the ready-app version path.

This is the code that decides which image a ready-made app installs. Every change below compiles,
leaves the screen looking correct, and silently deploys something other than what was chosen — which
is the exact class of failure the versioning model was built to prevent.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
SERVICE = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "TemplateDeploymentService.cs"
CATALOG = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "ReadyAppCatalog.cs"
ARCH = ROOT / "src" / "Harbora.Infrastructure" / "Templates" / "HostArchitecture.cs"
FACT = ROOT / "src" / "Harbora.Infrastructure" / "Monitoring" / "ReportedFact.cs"

FILTER = ("FullyQualifiedName~TemplateVersionDeploymentTests|"
          "FullyQualifiedName~ReadyAppCatalogTests|"
          "FullyQualifiedName~HostArchitectureTests|"
          "FullyQualifiedName~VersionSelectionTests")

MUTANTS = [
    (SERVICE, "manifest tag deployed instead of the pinned digest",
     'PrebuiltImage = version is null\n                ? manifest.Image\n                : VersionSelection.PinnedImage(version) ?? manifest.Image,',
     'PrebuiltImage = manifest.Image,'),

    (SERVICE, "deployed version not recorded on the app",
     'TemplateVersionId = version?.Id,',
     'TemplateVersionId = null,'),

    (SERVICE, "refusal rules skipped for an explicit choice",
     'if (VersionSelection.Refuse(version, nodeArchitecture) is { } refusal)',
     'if (VersionSelection.Refuse(version, nodeArchitecture) is { } refusal && false)'),

    (SERVICE, "server architecture never consulted",
     'var version = await ResolveVersionAsync(template.Id, request.VersionId, server.Architecture, ct);',
     'var version = await ResolveVersionAsync(template.Id, request.VersionId, null, ct);'),

    (SERVICE, "no offerable version falls back to the manifest",
     'return VersionSelection.Default(versions, nodeArchitecture)\n            ?? throw new InvalidOperationException(\n                "No version of this template can be deployed on this server yet.");',
     'return VersionSelection.Default(versions, nodeArchitecture);'),

    (SERVICE, "a version id for a template with none is ignored",
     'if (versionId is not null)\n                throw new InvalidOperationException("That version does not belong to this template.");',
     'if (versionId is not null && false)\n                throw new InvalidOperationException("That version does not belong to this template.");'),

    (SERVICE, "a version from another template is accepted",
     'var version = versions.FirstOrDefault(v => v.Id == chosen)\n                ?? throw new InvalidOperationException("That version does not belong to this template.");',
     'var version = versions.FirstOrDefault(v => v.Id == chosen) ?? versions[0];'),

    (CATALOG, "catalogue manifests lose their image again",
     'return $"{{\\"image\\":{J(image)},\\"port\\":{port}',
     'return $"{{\\"port\\":{port}'),

    (CATALOG, "every version manifest names the recommended image",
     'ManifestJson = Manifest($"{repository}:{v.Version}", port, healthPath, volumes, env, requires, website, docs)',
     'ManifestJson = Manifest($"{repository}:{offered.Version}", port, healthPath, volumes, env, requires, website, docs)'),

    (CATALOG, "catalogue image taken from an arbitrary version",
     'var offered = versions.FirstOrDefault(v => v.Lifecycle == VersionLifecycle.Recommended);\n        if (offered.Version is null) offered = versions.OrderBy(v => v.Lifecycle).First();',
     'var offered = versions[versions.Count - 1];'),

    (ARCH, "kernel names left untranslated",
     '"x86_64" or "x86-64" or "amd64" => "amd64",',
     '"amd64" => "amd64",'),

    (ARCH, "an unreported architecture becomes amd64",
     'if (string.IsNullOrWhiteSpace(reported)) return null;',
     'if (string.IsNullOrWhiteSpace(reported)) return "amd64";'),

    (ARCH, "an unfamiliar architecture is dropped",
     '_ => value',
     '_ => null'),

    (FACT, "a silent report erases what was known",
     'string.IsNullOrWhiteSpace(reported) ? current : reported.Trim();',
     'reported;'),

    (FACT, "a new report is ignored",
     'string.IsNullOrWhiteSpace(reported) ? current : reported.Trim();',
     'current;'),
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
