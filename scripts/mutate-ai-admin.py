"""Mutation pass over the AI administration controller.

Each entry below is a change that leaves the code compiling and the feature looking fine. If the
suite still passes with one applied, the test that was supposed to cover it does not.

The file is restored after every run, and rewritten with a fresh timestamp so the next build cannot
reuse a stale assembly built from the mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
TARGET = ROOT / "src" / "Harbora.Web" / "Controllers" / "AiAdminController.cs"
FILTER = "FullyQualifiedName~AiAdmin"

MUTANTS = [
    ("SSRF guard removed",
     'if (Harbora.Infrastructure.Ai.AiUpstreamUrl.Build(baseUrl, "models") is null)',
     'if (Harbora.Infrastructure.Ai.AiUpstreamUrl.Build(baseUrl, "models") is null && false)'),

    ("token stored in plaintext",
     'EncryptedToken = protector.Protect(token.Trim()),',
     'EncryptedToken = token.Trim(),'),

    ("empty token accepted on add",
     'TempData["Error"] = "A token is required.";',
     'TempData["Error"] = "A token is required."; if (false)'),

    ("empty replacement wipes the live token",
     '''if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = "A replacement token is required.";
            return RedirectToAction(nameof(Index));
        }''',
     '''if (string.IsNullOrWhiteSpace(token) && false)
        {
            TempData["Error"] = "A replacement token is required.";
            return RedirectToAction(nameof(Index));
        }'''),

    ("rotation leaves the old failure count",
     'credential.ConsecutiveFailures = 0;',
     'credential.ConsecutiveFailures = credential.ConsecutiveFailures;'),

    ("rotation leaves the circuit parked",
     'credential.RateLimitedUntil = null;',
     'credential.RateLimitedUntil = credential.RateLimitedUntil;'),

    ("toggle does nothing",
     'credential.IsEnabled = !credential.IsEnabled;',
     'credential.IsEnabled = credential.IsEnabled;'),

    ("saved model is not protected from registry sync",
     'model.IsManuallyManaged = true;',
     'model.IsManuallyManaged = false;'),

    ("plan keeps the models that were unticked",
     'foreach (var existing in plan.Models.Where(m => !wanted.Contains(m.AiModelId)).ToList())',
     'foreach (var existing in plan.Models.Where(m => false).ToList())'),

    ("spend window widened to sixty days",
     'clock.UtcNow.AddDays(-30)',
     'clock.UtcNow.AddDays(-60)'),

    ("failure list widened to thirty days",
     'var since = clock.UtcNow.AddDays(-7);',
     'var since = clock.UtcNow.AddDays(-30);'),

    ("client errors dropped from the failure list",
     'u.CreatedAt >= since && u.StatusCode >= 400',
     'u.CreatedAt >= since && u.StatusCode >= 500'),
]

original = TARGET.read_text(encoding="utf-8")
survivors = []

for name, old, new in MUTANTS:
    if old not in original:
        print(f"SKIP  {name}: pattern not found")
        survivors.append(name + " (pattern not found)")
        continue

    TARGET.write_text(original.replace(old, new, 1), encoding="utf-8")
    time.sleep(1.1)  # a same-second write can be treated as unchanged by the build
    result = subprocess.run(
        ["dotnet", "test", "tests/Harbora.Tests/Harbora.Tests.csproj", "--nologo", "-v", "q",
         "--filter", FILTER],
        cwd=ROOT, capture_output=True, text=True)

    caught = result.returncode != 0
    print(("CAUGHT" if caught else "SURVIVED") + f"  {name}")
    if not caught:
        survivors.append(name)

    TARGET.write_text(original, encoding="utf-8")
    time.sleep(1.1)

# Restored and rebuilt, so the tree is not left holding a green result produced by mutated code.
subprocess.run(["dotnet", "build", "src/Harbora.Web/Harbora.Web.csproj", "--nologo", "-v", "q"],
               cwd=ROOT, check=True)

print()
print(f"{len(MUTANTS) - len(survivors)}/{len(MUTANTS)} caught")
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
