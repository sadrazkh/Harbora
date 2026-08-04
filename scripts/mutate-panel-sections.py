"""Mutation pass over Simple mode inside pages.

Simple mode folds; it must never remove, and it must never fold a block the server has just
complained about. Both failures leave a form that looks fine and cannot be completed.

Files are restored after every run and rewritten with a fresh timestamp so the next build cannot
reuse an assembly compiled from mutated source.
"""
import subprocess, sys, time, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULE = ROOT / "src" / "Harbora.Infrastructure" / "Navigation" / "PanelSections.cs"
CREATE = ROOT / "src" / "Harbora.Web" / "Views" / "Apps" / "Create.cshtml"
DEPLOY = ROOT / "src" / "Harbora.Web" / "Views" / "Templates" / "Deploy.cshtml"
START = ROOT / "src" / "Harbora.Web" / "Views" / "Shared" / "Design" / "_AdvancedStart.cshtml"

FILTER = "FullyQualifiedName~PanelSectionTests|FullyQualifiedName~UiBaselineTests"

MUTANTS = [
    (RULE, "a rejected form keeps its blocks folded",
     'hasErrors || mode == PanelMode.Advanced;',
     'mode == PanelMode.Advanced;'),

    (RULE, "advanced mode folds too",
     'hasErrors || mode == PanelMode.Advanced;',
     'hasErrors;'),

    (RULE, "everything is always open and simple mode does nothing",
     'hasErrors || mode == PanelMode.Advanced;',
     'true;'),

    (RULE, "the modes are the wrong way round",
     'hasErrors || mode == PanelMode.Advanced;',
     'hasErrors || mode == PanelMode.Simple;'),

    (CREATE, "an advanced field is dropped in the name of simplicity",
     '<label class="form-field"><span>@(isFa ? "پورت داخلی" : "Container port")</span><input asp-for="ContainerPort" type="number" min="1" max="65535" class="form-control" /></label>',
     ''),

    (CREATE, "the cron fields are dropped",
     '<label data-cron-only class="form-field hidden"><span>@(isFa ? "زمان‌بندی Cron" : "Cron schedule")</span><input asp-for="CronExpression" placeholder="0 3 * * *" class="form-control" dir="ltr" /></label>',
     ''),

    (DEPLOY, "a folded picker stops saying which version installs",
     '@if (!versionsOpen && selectedVersion is not null)',
     '@if (false && selectedVersion is not null)'),

    (DEPLOY, "the version disclosure decides for itself",
     '<details class="advanced-panel" open="@versionsOpen">',
     '<details class="advanced-panel" open="@(await PanelModes.GetAsync() == Harbora.Domain.Identity.PanelMode.Advanced)">'),

    (START, "the shared disclosure is always folded",
     '<details class="advanced-panel" open="@Model.Open">',
     '<details class="advanced-panel">'),
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
