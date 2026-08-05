"""Mutation pass over bucket provisioning.

Two rules. `BucketPolicy` is the document deciding whether a leaked key is one bucket or every
tenant's objects — a resource of "arn:aws:s3:::*" looks almost identical to the correct one.
`BucketCommands` carries a root password, an access key and a policy document into a shell, so the
same property the volume browser needs holds here: the script is a constant and every value is a
positional argument.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
POLICY = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "BucketPolicy.cs"
CMD = ROOT / "src" / "Harbora.Infrastructure" / "Storage" / "BucketCommands.cs"

FILTER = "FullyQualifiedName~BucketProvisioningTests"

MUTANTS = [
    # --- the policy ---
    (POLICY, "the policy grants every bucket on the platform",
     '$"arn:aws:s3:::{bucket}",\n                        $"arn:aws:s3:::{bucket}/*"',
     '"arn:aws:s3:::*"'),

    (POLICY, "the bucket itself is not covered, so a client cannot list it",
     '$"arn:aws:s3:::{bucket}",\n                        $"arn:aws:s3:::{bucket}/*"',
     '$"arn:aws:s3:::{bucket}/*"'),

    (POLICY, "the objects are not covered, so a client can list and read nothing",
     '$"arn:aws:s3:::{bucket}",\n                        $"arn:aws:s3:::{bucket}/*"',
     '$"arn:aws:s3:::{bucket}"'),

    (POLICY, "a policy is built for a name that is not a bucket",
     'if (!BucketName.IsValid(bucket))\n            throw new ArgumentException("A policy must not be built for a name that is not a bucket.", nameof(bucket));',
     ''),

    (POLICY, "the scope check passes anything",
     'if (!allowed.Contains(resource?.GetValue<string>())) return false;',
     ''),

    (POLICY, "the scope check ignores a statement with no resources",
     'if (statement?["Resource"] is not JsonArray resources) return false;',
     'if (statement?["Resource"] is not JsonArray resources) continue;'),

    (POLICY, "every bucket shares one policy name",
     '$"{bucket}-rw"',
     '"harbora-rw"'),

    # --- the commands ---
    (CMD, "the bucket name is interpolated into the script",
     'public static IReadOnlyList<string> Measure(\n        string endpoint, string rootUser, string rootPassword, string bucket) =>\n        ["sh", "-c", MeasureScript, "sh", endpoint, rootUser, rootPassword, bucket];',
     'public static IReadOnlyList<string> Measure(\n        string endpoint, string rootUser, string rootPassword, string bucket) =>\n        ["sh", "-c", MeasureScript + " # " + bucket, "sh", endpoint, rootUser, rootPassword, bucket];'),

    (CMD, "argv zero is dropped, shifting every argument by one",
     '["sh", "-c", MeasureScript, "sh", endpoint, rootUser, rootPassword, bucket];',
     '["sh", "-c", MeasureScript, endpoint, rootUser, rootPassword, bucket];'),

    (CMD, "removing a bucket forces it, deleting somebody's objects",
     '"mc rb " + Alias + "/\\"$4\\" >/dev/null 2>&1 || exit 21; "',
     '"mc rb --force " + Alias + "/\\"$4\\" >/dev/null 2>&1 || exit 21; "'),

    (CMD, "the key that could reach a deleted bucket survives",
     '"mc admin user remove " + Alias + " \\"$5\\" >/dev/null 2>&1 || true; "',
     ''),

    (CMD, "no quota means a quota of nothing",
     'bytes <= 0 ? string.Empty : $"{bytes}B"',
     '$"{bytes}B"'),

    (CMD, "a negative quota is sent to the server",
     'bytes <= 0 ? string.Empty',
     'bytes < 0 ? string.Empty'),

    (CMD, "the first figure wins, so one prefix is reported as the whole bucket",
     'found = bytes;',
     'found ??= bytes;'),

    (CMD, "unreadable output is reported as an empty bucket",
     'if (string.IsNullOrWhiteSpace(output)) return null;',
     'if (string.IsNullOrWhiteSpace(output)) return 0;'),

    (CMD, "a line with no json in it is read as a measurement",
     'if (start < 0) continue;',
     ''),

    (CMD, "a negative size is accepted as a measurement",
     'size.GetValue<long>() is var bytes && bytes >= 0',
     'size.GetValue<long>() is var bytes'),
]

originals = {p: p.read_text(encoding="utf-8") for p in {POLICY, CMD}}
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
