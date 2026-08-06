"""Mutation pass over the terminal access rule.

A shell inside a customer's container is the widest door this platform has: the filesystem, the
environment (which holds the database password) and the network, all at once. Every guard the rest
of the panel maintains is downstream of this one decision.

Two kinds of mutant here. The first drops a condition, which opens the door. The second reorders the
questions, which is subtler and just as real: answering "there is no container running" before
asking who is calling turns a refusal into a probe for which applications exist and whether they are
up.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source. A run killed part-way leaves a mutant behind, so the
marker below refuses to start a second run until the file is restored.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Terminals" / "TerminalAccess.cs"
MARKER = ROOT / "scripts" / ".mutation-in-progress"

FILTER = "FullyQualifiedName~TerminalAccessTests"

DECIDE = (
    "        if (!featureEnabled) return TerminalRefusal.FeatureOff;\n"
    "        if (!mayManage) return TerminalRefusal.NotAllowed;\n"
    "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n"
    "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n"
    "        return TerminalRefusal.None;"
)

MUTANTS = [
    # --- the door itself ---
    ("the feature switch is not consulted, so terminals ship turned on",
     "        if (!featureEnabled) return TerminalRefusal.FeatureOff;\n",
     ""),

    ("anyone who can see the application can open a shell in it",
     "        if (!mayManage) return TerminalRefusal.NotAllowed;\n",
     ""),

    ("an application on a node opens a terminal onto nothing",
     "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n",
     ""),

    ("a stopped application is attached to anyway",
     "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n",
     ""),

    ("every refusal becomes an approval",
     DECIDE,
     "        return TerminalRefusal.None;"),

    # --- the order the questions are asked in ---
    ("the state of somebody's container is answered before who is asking",
     DECIDE,
     "        if (!featureEnabled) return TerminalRefusal.FeatureOff;\n"
     "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n"
     "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n"
     "        if (!mayManage) return TerminalRefusal.NotAllowed;\n"
     "        return TerminalRefusal.None;"),

    ("the feature switch is asked last, so a disabled platform still refuses in detail",
     DECIDE,
     "        if (!mayManage) return TerminalRefusal.NotAllowed;\n"
     "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n"
     "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n"
     "        if (!featureEnabled) return TerminalRefusal.FeatureOff;\n"
     "        return TerminalRefusal.None;"),

    ("a stopped application on a node is reported as stopped rather than as a node",
     "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n"
     "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n",
     "        if (!hasRunningContainer) return TerminalRefusal.NotRunning;\n"
     "        if (!isLocalServer) return TerminalRefusal.NotLocal;\n"),

    # --- when a session ends ---
    ("an idle session is never closed",
     "now - lastActivity >= IdleTimeout || now - startedAt >= MaxDuration;",
     "now - startedAt >= MaxDuration;"),

    ("a session open all afternoon is kept as long as somebody is typing",
     "now - lastActivity >= IdleTimeout || now - startedAt >= MaxDuration;",
     "now - lastActivity >= IdleTimeout;"),

    ("the idle clock is read from the start, so a session in use is cut off",
     "now - lastActivity >= IdleTimeout",
     "now - startedAt >= IdleTimeout"),

    ("the ceiling is measured from the last keystroke, so a session never reaches it",
     "now - startedAt >= MaxDuration",
     "now - lastActivity >= MaxDuration"),

    ("the idle boundary is off by one, so the timeout is never exactly reached",
     "now - lastActivity >= IdleTimeout",
     "now - lastActivity > IdleTimeout"),

    ("the ceiling boundary is off by one",
     "now - startedAt >= MaxDuration",
     "now - startedAt > MaxDuration"),

    ("nothing ever closes a session",
     "        now - lastActivity >= IdleTimeout || now - startedAt >= MaxDuration;",
     "        false;"),

    # --- what is run, and how big ---
    ("the shell is not exec'd, so the session outlives it",
     '"exec /bin/bash 2>/dev/null || exec /bin/sh"',
     '"/bin/bash 2>/dev/null || /bin/sh"'),

    ("there is no fallback, so an image without bash has no terminal",
     '"exec /bin/bash 2>/dev/null || exec /bin/sh"',
     '"exec /bin/bash"'),

    ("a window reported as zero is passed to docker, which refuses it",
     "((uint)Math.Clamp(columns, 20, 500), (uint)Math.Clamp(rows, 5, 200));",
     "((uint)Math.Max(columns, 0), (uint)Math.Max(rows, 0));"),

    ("an enormous window is passed through",
     "(uint)Math.Clamp(columns, 20, 500)",
     "(uint)Math.Max(columns, 20)"),

    ("columns and rows are swapped",
     "((uint)Math.Clamp(columns, 20, 500), (uint)Math.Clamp(rows, 5, 200));",
     "((uint)Math.Clamp(rows, 20, 500), (uint)Math.Clamp(columns, 5, 200));"),
]

if MARKER.exists():
    print(f"A previous run did not finish. Restore {RULE} and delete {MARKER}.")
    sys.exit(2)

original = RULE.read_text(encoding="utf-8")
MARKER.write_text("restore the file named in mutate-terminal.py", encoding="utf-8")

survivors = []
only = sys.argv[1] if len(sys.argv) > 1 else None

for name, old, new in MUTANTS:
    if only and only not in name:
        continue
    if old not in original:
        print(f"SKIP  {name}: pattern not found")
        survivors.append(name + " (pattern not found)")
        continue

    RULE.write_text(original.replace(old, new, 1), encoding="utf-8")
    time.sleep(1.1)
    result = subprocess.run(
        ["dotnet", "test", "tests/Harbora.Tests/Harbora.Tests.csproj", "--nologo", "-v", "q",
         "--filter", FILTER],
        cwd=ROOT, capture_output=True, text=True)

    caught = result.returncode != 0
    print(("CAUGHT" if caught else "SURVIVED") + f"  {name}")
    if not caught:
        survivors.append(name)

    RULE.write_text(original, encoding="utf-8")
    time.sleep(1.1)

MARKER.unlink()
subprocess.run(["dotnet", "build", "Harbora.slnx", "--nologo", "-v", "q"], cwd=ROOT, check=True)

ran = len([m for m in MUTANTS if not only or only in m[0]])
print()
print(f"{ran - len(survivors)}/{ran} caught" + (f" (filtered on '{only}')" if only else ""))
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
