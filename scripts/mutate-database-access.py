"""Mutation pass over external database access.

The two things being protected: that nobody is handed a connection string to a gateway that was
never opened, and that a grant id arriving on one database's URL cannot act on another's.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "ExternalAccessAvailability.cs"
ACTIONS = ROOT / "src" / "Harbora.Web" / "Controllers" / "DatabaseAccessActions.cs"
FAKE = ROOT / "src" / "Harbora.Infrastructure" / "Nodes" / "FakeNodeAgentClient.cs"

FILTER = ("FullyQualifiedName~ExternalAccessAvailabilityTests|"
          "FullyQualifiedName~DatabaseAccessPageTests|"
          "FullyQualifiedName~DatabaseAccessLifecycleTests|"
          "FullyQualifiedName~DatabaseAccessPolicyTests")

MUTANTS = [
    (RULE, "a simulated agent is allowed to issue access",
     'if (node.IsSimulated)',
     'if (node.IsSimulated && false)'),

    (RULE, "a stopped database can be opened",
     'if (service.Status != ServiceStatus.Running)',
     'if (service.Status != ServiceStatus.Running && false)'),

    (RULE, "a database that no longer exists is treated as fine",
     'if (service is null)\n            return new AccessUnavailable("That database no longer exists.", "این دیتابیس دیگر وجود ندارد.");',
     'if (service is null)\n            return null;'),

    (RULE, "the simulation reason is reported as a stopped database",
     'if (node.IsSimulated)\n            return new AccessUnavailable(\n                "External access needs the Harbora node agent, which is not configured on this installation. " +\n                "Nothing would be reachable, so nothing is issued.",',
     'if (node.IsSimulated)\n            return new AccessUnavailable(\n                "That database is not running." +\n                "",'),

    (FAKE, "the fake stops admitting it is a fake",
     'public bool IsSimulated => true;',
     'public bool IsSimulated => false;'),

    (ACTIONS, "issue no longer re-checks availability at submit time",
     'if (ExternalAccessAvailability.Refuse(node, service) is { } unavailable)\n            return View("Access", await BuildAccessPageAsync(id, ct, error: IsFa ? unavailable.ReasonFa : unavailable.Reason));\n\n        var result = await databaseAccess.IssueAsync(',
     'if (false)\n            return View("Access", await BuildAccessPageAsync(id, ct, error: "x"));\n\n        var result = await databaseAccess.IssueAsync('),

    (ACTIONS, "a grant is found without checking which database it belongs to",
     'g => g.Id == grantId && g.ManagedServiceId == serviceId && g.WorkspaceId == WorkspaceId, ct);',
     'g => g.Id == grantId && g.WorkspaceId == WorkspaceId, ct);'),

    (ACTIONS, "a grant is found without checking the workspace",
     'g => g.Id == grantId && g.ManagedServiceId == serviceId && g.WorkspaceId == WorkspaceId, ct);',
     'g => g.Id == grantId && g.ManagedServiceId == serviceId, ct);'),

    # The workspace filter inside FindDatabaseAsync is defence in depth: every current action is
    # already gated by CanSeeServiceAsync or Guard, so removing it changes no observable behaviour
    # and no test can catch it. What can be caught, and is what would actually go wrong, is a new
    # action querying the sets directly and skipping both.
    (ACTIONS, "an action queries the database set directly",
     'var service = await FindDatabaseAsync(id, ct);\n        if (service is null) return NotFound();',
     'var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id, ct);\n        if (service is null) return NotFound();'),

    (ACTIONS, "an action queries the grant set directly",
     'var grant = await FindGrantAsync(id, grantId, ct);\n        if (grant is null) return NotFound();\n\n        var error = await databaseAccess.ExtendAsync',
     'var grant = await db.DatabaseAccessGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);\n        if (grant is null) return NotFound();\n\n        var error = await databaseAccess.ExtendAsync'),

    (ACTIONS, "revoke is blocked while the agent is simulated",
     'await databaseAccess.CloseAsync(\n            grant, DatabaseAccessStatus.Revoked, "Revoked from the panel.", User.Identity?.Name, ct);',
     'if (ExternalAccessAvailability.Refuse(node, await FindDatabaseAsync(id, ct)) is null)\n            await databaseAccess.CloseAsync(\n                grant, DatabaseAccessStatus.Revoked, "Revoked from the panel.", User.Identity?.Name, ct);'),

    (ACTIONS, "closed grants are hidden from the page",
     '.Where(g => g.ManagedServiceId == id && g.WorkspaceId == WorkspaceId)',
     '.Where(g => g.ManagedServiceId == id && g.WorkspaceId == WorkspaceId && g.Status == DatabaseAccessStatus.Active)'),
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
