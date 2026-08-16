"""Mutation pass over the attention rules.

The panel this feeds is the first thing on the dashboard, and its failure modes are quiet: a level
swapped and the wrong thing read first, a key swapped and the wrong sentence shown, the cap dropped
and the list became a wall. Every mutant below is one of those quiet failures; a survivor means the
tests would let it ship.

Files are restored after every run. The build at the end recompiles the restored source so the next
test run cannot reuse an assembly built from a mutant.
"""
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Dashboard" / "Attention.cs"

FILTER = "FullyQualifiedName~AttentionRulesTests|FullyQualifiedName~ServerStringsLocalizationTests"

MUTANTS = [
    ("a failed deployment is only a warning",
     "Level = AttentionLevel.Critical,\n                TitleKey = DeployFailedTitle",
     "Level = AttentionLevel.Warning,\n                TitleKey = DeployFailedTitle"),

    ("an expired certificate is only a warning",
     "Level = AttentionLevel.Critical,\n                    TitleKey = CertificateExpiredTitle",
     "Level = AttentionLevel.Warning,\n                    TitleKey = CertificateExpiredTitle"),

    ("a certificate merely due reads as expired",
     "TitleKey = CertificateAttentionTitle, TitleArgs = [host],\n                    DetailKey = CertificateExpiringDetail",
     "TitleKey = CertificateExpiredTitle, TitleArgs = [host],\n                    DetailKey = CertificateExpiringDetail"),

    ("the real error is discarded for the generic fallback",
     "DetailText = Summarise(error),\n                DetailKey = Summarise(error) is null ? DeployFailedDetail : null,\n                ActionKey = DeployFailedAction",
     "DetailText = null,\n                DetailKey = DeployFailedDetail,\n                ActionKey = DeployFailedAction"),

    ("a broken backup channel points at alerts",
     'ActionUrl = kind == ChannelKind.BackupDelivery ? "/backups" : "/monitoring"',
     'ActionUrl = "/monitoring"'),

    ("the disk threshold is gone",
     "if (facts.DiskUsedRatio >= diskWarnRatio)",
     "if (facts.DiskUsedRatio >= 0)"),

    ("the disk percentage is inverted free space",
     'DetailKey = DiskDetail, DetailArgs = [$"{facts.DiskUsedRatio * 100:0}"]',
     'DetailKey = DiskDetail, DetailArgs = [$"{(1 - facts.DiskUsedRatio) * 100:0}"]'),

    ("severity ordering is whatever the rules emitted",
     ".OrderBy(i => (int)i.Level)\n            .Take(MaxItems)",
     ".Take(MaxItems)"),

    ("the cap is gone and the list becomes a wall",
     ".OrderBy(i => (int)i.Level)\n            .Take(MaxItems)",
     ".OrderBy(i => (int)i.Level)"),

    ("the backup nudge fires for an empty workspace",
     "if (facts.HasAnyApp && !facts.HasAnyBackupSchedule)",
     "if (!facts.HasAnyBackupSchedule)"),

    ("a key is emitted that the vocabulary does not declare",
     'public const string NoBackupsTitle = "No scheduled backups";',
     'public const string NoBackupsTitle = "No scheduled backups (undeclared)";'),
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
