"""Mutation pass over external database access and the deployment progress bar.

The first half decides what gets published to the internet and what goes into SQL. The second is the
bar that spent its whole life not moving. Both are places where a wrong answer is silent.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
GW = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "TcpGatewayPlan.cs"
SQL = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "DatabaseGrantSql.cs"
AVAIL = ROOT / "src" / "Harbora.Infrastructure" / "Services" / "ExternalAccessAvailability.cs"
STEPS = ROOT / "src" / "Harbora.Infrastructure" / "Deployments" / "DeploymentSteps.cs"
PANEL = ROOT / "src" / "Harbora.Infrastructure" / "Navigation" / "PanelSections.cs"

FILTER = ("FullyQualifiedName~TcpGatewayPlanTests|"
          "FullyQualifiedName~ExternalAccessAvailabilityTests|"
          "FullyQualifiedName~DatabaseAccessPageTests|"
          "FullyQualifiedName~DeploymentStepsTests|"
          "FullyQualifiedName~PanelSectionTests")

MUTANTS = [
    # ---- what reaches the internet ----
    (GW, "a bad allowlist entry opens the door instead of closing it",
     'if (!IsCidrOrAddress(entry)) return null;',
     'if (!IsCidrOrAddress(entry)) continue;'),

    (GW, "the allowlist is never enforced",
     'config.AppendLine("  tcp-request connection reject if !allowed");',
     ''),

    (GW, "an oversized prefix is accepted",
     'return prefix >= 0 && prefix <= bits;',
     'return prefix >= 0;'),

    (GW, "a malformed address parses",
     'if (slash < 0) return IPAddress.TryParse(entry, out _);',
     'if (slash < 0) return true;'),

    (GW, "two grants can take the same port",
     'if (!used.Contains(port)) return port;',
     'return port;'),

    (GW, "a full band hands out a port past the end of it",
     'return null;\n    }\n\n    /// <summary>\n    /// The hostname to hand out',
     'return LastPort + 1;\n    }\n\n    /// <summary>\n    /// The hostname to hand out'),

    (GW, "the container name is truncated and can collide",
     'public static string ContainerName(Guid grantId) => $"harbora-gw-{grantId:N}";',
     'public static string ContainerName(Guid grantId) => $"harbora-gw-{grantId:N}"[..24];'),

    (GW, "the proxy config travels on the command line",
     '"printf \'%s\' \\"$HARBORA_GATEWAY_CONFIG\\" > /tmp/haproxy.cfg && exec haproxy -f /tmp/haproxy.cfg -db"',
     '"exec haproxy -f /dev/stdin -db <<< \'frontend db\'"'),

    (GW, "a hostname is built from an unsanitised name",
     'var slug = Slug(serviceSlug);',
     'var slug = (serviceSlug ?? string.Empty).Trim();'),

    # ---- what reaches SQL ----
    (SQL, "quotes are allowed into a statement",
     "&& value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');",
     "&& !value.Contains(';');"),

    (SQL, "the safety check is skipped entirely",
     'if (!IsSafe(username) || !IsSafe(password) || !IsSafe(database) || !IsSafe(adminUser)) return null;',
     ''),

    (SQL, "the drop path stops checking its input",
     'if (!IsSafe(username) || !IsSafe(database) || !IsSafe(adminUser)) return null;',
     ''),

    (SQL, "an engine with no login support is opened anyway",
     'type is ManagedServiceType.PostgreSql or ManagedServiceType.MySql or ManagedServiceType.MariaDb;',
     'true;'),

    (SQL, "an empty value passes as safe",
     '!string.IsNullOrEmpty(value)',
     'value is not null'),

    (SQL, "the admin password goes back into argv",
     '"mariadb", "-h", host, "-P", port.ToString(), "-u", adminUser,\n                "-e", $"CREATE USER',
     '"mariadb", "-h", host, "-P", port.ToString(), "-u", adminUser, "-padminsecret",\n                "-e", $"CREATE USER'),

    (SQL, "dropping a login that is gone becomes an error",
     '"psql", "-h", host, "-p", port.ToString(), "-U", adminUser, "-d", database,',
     '"psql", "-v", "ON_ERROR_STOP=1", "-h", host, "-p", port.ToString(), "-U", adminUser, "-d", database,'),

    (AVAIL, "an unsupported engine is no longer refused",
     'if (service is not null && !DatabaseGrantSql.Supports(service.Type))',
     'if (service is not null && !DatabaseGrantSql.Supports(service.Type) && false)'),

    (AVAIL, "a stopped database can be published",
     'if (service.Status != ServiceStatus.Running)',
     'if (service.Status != ServiceStatus.Running && false)'),

    (AVAIL, "a simulated agent blocks an install that can open the port itself",
     'if (!canOpenLocally && node.IsSimulated)',
     'if (node.IsSimulated)'),

    # ---- the progress bar ----
    (STEPS, "pushing gets a step of its own and the bar jumps back",
     'DeploymentStatus.Pushing => 1,',
     'DeploymentStatus.Pushing => 4,'),

    (STEPS, "a failure is given a position on the bar",
     'DeploymentStatus.RolledBack => 4,\n        _ => null',
     'DeploymentStatus.RolledBack => 4,\n        _ => 4'),

    (STEPS, "the bar keeps animating after the deployment ended",
     'if (status == DeploymentStatus.Succeeded) return StepState.Done;',
     'if (status == DeploymentStatus.Succeeded) return StepState.Active;'),

    (STEPS, "a rollback claims the release shipped",
     'return step < Count - 1 ? StepState.Done : StepState.Failed;',
     'return StepState.Done;'),

    (STEPS, "steps before the current one are not marked done",
     'if (step < active) return StepState.Done;',
     'if (step < active) return StepState.Pending;'),

    (STEPS, "two steps are active at once",
     'return step == active ? StepState.Active : StepState.Pending;',
     'return step >= active ? StepState.Active : StepState.Pending;'),

    (STEPS, "the map handed to the browser loses a status",
     '.Where(s => s.Index is not null)',
     '.Where(s => s.Index is not null && s.Name != "Pushing")'),

    (STEPS, "a terminal status is not reported as terminal",
     'status is DeploymentStatus.Succeeded or DeploymentStatus.Failed\n               or DeploymentStatus.Cancelled or DeploymentStatus.RolledBack;',
     'status is DeploymentStatus.Succeeded;'),

    # ---- simple mode ----
    (PANEL, "a rejected form stays folded over the field it complained about",
     'hasErrors || mode == PanelMode.Advanced;',
     'mode == PanelMode.Advanced;'),

    (PANEL, "simple mode shows everything advanced does",
     'public static bool ShowsPlatformDetail(PanelMode mode) => mode == PanelMode.Advanced;',
     'public static bool ShowsPlatformDetail(PanelMode mode) => true;'),
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
