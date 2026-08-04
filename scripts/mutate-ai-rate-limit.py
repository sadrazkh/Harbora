"""Mutation pass over the AI gateway's per-minute limits.

These limits were stored, priced and displayed for two phases while nothing enforced them. Each
change below restores some version of that: the limiter is still there, still configured, still on
the plan page, and lets traffic through it should not.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
WINDOW = ROOT / "src" / "Harbora.Infrastructure" / "Ai" / "AiRateWindow.cs"
LIMITER = ROOT / "src" / "Harbora.Infrastructure" / "Ai" / "AiRateLimiter.cs"
DI = ROOT / "src" / "Harbora.Infrastructure" / "DependencyInjection.cs"

FILTER = "FullyQualifiedName~AiRateLimitTests"

MUTANTS = [
    (WINDOW, "off by one lets everyone have a free request a minute",
     'if (inMinute.Count >= plan.RequestsPerMinute)',
     'if (inMinute.Count > plan.RequestsPerMinute)'),

    (WINDOW, "off by one on the daily limit",
     'if (inDay.Count >= plan.RequestsPerDay)',
     'if (inDay.Count > plan.RequestsPerDay)'),

    (WINDOW, "the minute window becomes a fixed one",
     'var inMinute = inDay.Where(e => e.At > minuteStart).ToList();',
     'var inMinute = inDay.Where(e => e.At.Minute == now.Minute).ToList();'),

    (WINDOW, "the day window never expires anything",
     'var inDay = events.Where(e => e.At > dayStart).ToList();',
     'var inDay = events.ToList();'),

    (WINDOW, "the token rate limit stops being checked",
     'if (plan.TokensPerMinute > 0)',
     'if (plan.TokensPerMinute > 0 && false)'),

    (WINDOW, "concurrency stops being checked",
     'if (inFlight >= plan.ConcurrentRequests)',
     'if (inFlight > plan.ConcurrentRequests + 1000)'),

    (WINDOW, "a limit of zero opens instead of blocking",
     'if (plan.RequestsPerMinute <= 0)\n            return Blocked("rate_limit", "Your plan allows no requests.");',
     'if (plan.RequestsPerMinute <= 0)\n            return RateDecision.Ok;'),

    (WINDOW, "the minute limit is reported before the daily one",
     'var inDay = events.Where(e => e.At > dayStart).ToList();\n        if (inDay.Count >= plan.RequestsPerDay)',
     'var inDay = events.Where(e => e.At > dayStart).ToList();\n        if (inDay.Count >= plan.RequestsPerDay && false)'),

    (WINDOW, "a retry time of zero invites an immediate retry",
     'return seconds <= 1 ? 1 : (int)Math.Ceiling(seconds);',
     'return (int)seconds;'),

    (WINDOW, "refusals stop being 429",
     'new(new AiRefusal(429, code, message), retryAfter);',
     'new(new AiRefusal(403, code, message), retryAfter);'),

    (WINDOW, "pruning throws away the whole history",
     'return events.Where(e => e.At > cutoff).ToList();',
     'return [];'),

    (LIMITER, "requests are counted when they finish rather than when they start",
     'counters.Events.Add(new RateEvent(now, 0));\n            counters.InFlight++;',
     'counters.InFlight++;'),

    (LIMITER, "a slot is never released",
     'if (counters.InFlight > 0) counters.InFlight--;',
     '_ = counters.InFlight;'),

    (LIMITER, "a double dispose hands back a slot that was never taken",
     'if (Interlocked.Exchange(ref _released, 1) == 0)\n            _limiter.Release(_workspaceId);',
     '_limiter.Release(_workspaceId);'),

    (LIMITER, "reported tokens are appended instead of attached",
     'if (index >= 0) counters.Events[index] = counters.Events[index] with { Tokens = tokens };',
     'counters.Events.Add(new RateEvent(startedAt, tokens));'),

    (LIMITER, "tenants share one set of counters",
     'var counters = _byWorkspace.GetOrAdd(workspaceId, _ => new Counters { LastTouched = now });',
     'var counters = _byWorkspace.GetOrAdd(Guid.Empty, _ => new Counters { LastTouched = now });'),

    (LIMITER, "the sweep runs after the entry is created again",
     'Sweep(now);\n\n        var counters = _byWorkspace.GetOrAdd(workspaceId, _ => new Counters { LastTouched = now });',
     'var counters = _byWorkspace.GetOrAdd(workspaceId, _ => new Counters());\n        Sweep(now);'),

    (LIMITER, "the lock is dropped and two callers take the last slot",
     'lock (counters)\n        {\n            counters.LastTouched = now;',
     'if (true)\n        {\n            counters.LastTouched = now;'),

    (DI, "the limiter becomes scoped and forgets everything each request",
     'services.AddSingleton<Ai.AiRateLimiter>();',
     'services.AddScoped<Ai.AiRateLimiter>();'),
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
