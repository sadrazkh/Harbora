"""Mutation pass over which buckets get measured on a tick.

Measuring runs a container, so the sweep takes a few at a time. That makes the ordering
load-bearing: every mutation below either starves some bucket permanently — leaving a usage figure
that is never anything but unknown, which is the state automatic measurement exists to remove — or
spends a container on every bucket on every tick.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "BucketMeasurementSchedule.cs"

FILTER = "FullyQualifiedName~BucketMeasurementScheduleTests"

MUTANTS = [
    ("a never-measured bucket sorts last, so it is measured only once everything else is stale",
     '.OrderBy(b => b.MeasuredAt.HasValue)\n            .ThenBy(b => b.MeasuredAt ?? DateTimeOffset.MinValue)',
     '.OrderBy(b => b.MeasuredAt ?? DateTimeOffset.UtcNow)'),

    ("the order is whatever the database returned, so the same few are always measured",
     '.OrderBy(b => b.MeasuredAt.HasValue)\n            .ThenBy(b => b.MeasuredAt ?? DateTimeOffset.MinValue)',
     ''),

    ("newest first, so the stale ones are never reached",
     '.ThenBy(b => b.MeasuredAt ?? DateTimeOffset.MinValue)',
     '.ThenByDescending(b => b.MeasuredAt ?? DateTimeOffset.MinValue)'),

    ("a fresh measurement is taken again, spending a container to learn nothing",
     'b.MeasuredAt is not { } at || now - at >= window',
     'true'),

    ("the interval boundary is off by one, so it is never quite reached",
     'now - at >= window',
     'now - at > window'),

    ("a measurement dated in the future is treated as very old and measured on every tick",
     'now - at >= window',
     'now - at >= window || at > now'),

    ("the batch is ignored and every bucket is measured on every tick",
     '.Take(batch)',
     ''),

    ("the caller's interval is ignored in favour of the default",
     'var window = interval ?? DefaultInterval;',
     'var window = DefaultInterval;'),
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
