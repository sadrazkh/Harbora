"""Mutation pass over the CPU percentage both sides compute.

The control plane computes it for containers on its own host and the agent computes it for
containers on a node. It lives in the contract so there is one copy — two would mean the same
container reads differently depending on where it runs, and the difference gets blamed on the node.

Every mutation below produces a number that renders perfectly and is wrong in a way nobody would
question: an application reported idle while it is starting, a spike that never happened, or every
container on a runtime that does not report its core count reading as zero.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.NodeAgent.Contracts" / "ContainerCpu.cs"

FILTER = "FullyQualifiedName~ContainerCpuTests"

MUTANTS = [
    ("the first sample after a start reports an idle container instead of nothing",
     'if (systemDelta == 0) return null;',
     'if (systemDelta == 0) return 0;'),

    ("no interval at all is divided by anyway",
     'if (systemDelta == 0) return null;',
     ''),

    ("a wrapped counter becomes a spike nobody can explain",
     'if (cpuDelta > systemDelta) return null;',
     ''),

    ("the reset guard also throws away a container using the whole host",
     'if (cpuDelta > systemDelta) return null;',
     'if (cpuDelta >= systemDelta) return null;'),

    ("an unreported core count reads every container as idle",
     'var cores = onlineCpus == 0 ? 1UL : onlineCpus;',
     'var cores = onlineCpus;'),

    ("the reading is not scaled per core, so a busy host looks quiet",
     'return Math.Round((double)cpuDelta / systemDelta * cores * 100.0, 2);',
     'return Math.Round((double)cpuDelta / systemDelta * 100.0, 2);'),

    ("the percentage is a fraction, off by two orders of magnitude",
     '* cores * 100.0, 2);',
     '* cores, 2);'),

    ("integer division throws away everything below one",
     '(double)cpuDelta / systemDelta',
     'cpuDelta / systemDelta'),
]

original = RULE.read_text(encoding="utf-8")
survivors = []

for name, old, new in MUTANTS:
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

subprocess.run(["dotnet", "build", "Harbora.slnx", "--nologo", "-v", "q"], cwd=ROOT, check=True)

print()
print(f"{len(MUTANTS) - len(survivors)}/{len(MUTANTS)} caught")
for s in survivors:
    print("  survived:", s)
sys.exit(1 if survivors else 0)
