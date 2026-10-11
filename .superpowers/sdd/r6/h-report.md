# H — The assistant in the learning center (wave B, docs only) — report

Branch `r6/assistant-docs`, worktree `Harbora-r6-h`, based on master `d41247d`. Status: **DONE_WITH_CONCERNS**
(concerns at the end; the biggest is that the guide describes the write tools from F's and G's briefs, which
are not on this branch).

Commits (owner identity, no trailer):

```
98c27e0 Learning center: a Persian guide for the in-panel assistant, and the two gateway sentences ...
c1c397f Help control: the locked assistant page opens chapter 8, and the guide's figures are pinned ...
92d3fa3 READMEs: one paragraph on the assistant beside the AI preview note ...
```

plus the commit adding this report.

## What changed

| File | Change |
|---|---|
| `docs/tutorial/08-ai.md` | New section «دستیار هوشمند پنل» (subsections: what to ask, owner setup, «تست مدل عامل», turning it on per plan/workspace, the consent levels, after «اجرا», what it never does, the drawer, limits); one intro sentence pointing to it; the two fixes below. The gateway section is otherwise untouched. |
| `docs/tutorial/README.md` | Chapter 8's row names the new section; one sentence says the section has no screenshots because it was written from source (the reason the file already gives for chapters 10-13). |
| `src/Harbora.Infrastructure/Learning/HelpMap.cs` | `("/features/assistant", "08-ai")`, plus a comment on why `/settings` stays on chapter 9. |
| `README.md`, `README.fa.md` | One bullet each in "Known limitations", straight after the AI-service-preview bullet. |
| `tests/Harbora.Tests/AssistantGuideTests.cs` | **New, 3 tests** — the one thing beyond docs and HelpMap; see below. |

### The two fixes in the gateway section

1. **Provider types / localhost.** The code has two gateway types (`AiProviderType`: `OpenRouter`,
   `OpenAiCompatible`) and the form offers exactly those. `AiAdminController.SaveProvider` refuses a base URL
   that is not HTTPS or whose host `AiUpstreamUrl.IsForbiddenHost` rejects (localhost, loopback, 10/8,
   172.16/12, 192.168/16, 169.254/16, 0/8, fc00::/7, fe80::/10, `.internal`, `.local`). The chapter now lists the
   two types, says what the panel refuses, and says `http://localhost:11434` (no key) is true only for the two
   assistants (`AssistantAvailability.IsLocal`).
2. **"Assign a plan from Tenants or this page."** `TenantsController.AssignPlan` sets `Workspace.PlanId`, the
   *quota* plan, not an AI plan. `AiSubscription` (the workspace-to-AI-plan link the gateway and the user page
   read) is **never written by any code under `src/`** (a search for `new AiSubscription` and
   `AiSubscriptions.Add` under `src/` finds nothing; only tests create one). Step 4 now says so plainly instead
   of naming a screen.

### What the guide says, and where each claim comes from

Everything is taken from the code on this branch or from the C/D/E reports, except the write-tool examples
(see concerns). The facts worth knowing, since a reviewer can check them:

* **Button label.** The brief calls it «آزمون مدل»; the page's own Persian label is **«تست مدل عامل»**
  (`Settings/Index.cshtml`), so the guide uses that. Other labels are quoted from the views: «دستیار هوش مصنوعی»,
  «عامل هوش مصنوعی پنل», «مدل عامل», «مدل‌های جایگزین», «سقف درخواست روزانه», «تست اتصال»; the features console
  («پلتفرم › قابلیت‌ها», «بر اساس پلن», «استثنا برای یک ورک‌اسپیس», the four state names); the card («اجرا»,
  «لغو», «دیگر برای این کار نپرس», «برای تأیید بنویسید», the three badges).
* **Setup.** The master switch «روشن» is shared with the deployment explainer. Provider must be OpenAI-compatible
  (Anthropic is refused for the agent in the drawer, not on the settings page). A blank agent model falls back to
  «مدل». Daily limit: blank = 300, `0` = unlimited, Persian digits accepted, per workspace per UTC day, every
  model call counts (failed ones too).
* **Test button.** Six prompts (3 fa, 3 en), nine fake tools, nothing executed; about six provider requests, not
  counted against the workspace cap (the meter lives in `AgentConversationService`, the bench holds only the
  chat client); it tests the *saved* settings; the POST returns the page, so a refresh re-posts. The result words
  and the verdict thresholds (all pass / `passed*3 >= judged*2` / below) are `AgentModelBench`'s.
* **Entitlement.** `assistant` is Locked by default; I found no special case for the owner's own workspace in
  `FeatureGate`, `FeatureAccess` or `FeatureView`, and nothing seeds a grant, so the guide says "including your
  own workspace".
* **Consent.** Exactly D's: read runs; safe asks, remembered per (user, workspace, tool) — not across tools or
  colleagues; any non-remembered step makes the whole plan a card; dangerous always carded; destructive needs the
  exact name (case-sensitive, ends trimmed); every step is re-validated before anything runs; the first failure
  skips the rest; a plan lives 15 minutes; a new message supersedes a pending card; each executed step is audited.
* **Drawer.** E's: toggle beside the search box, Ctrl/Cmd+J, Esc, the side opposite the menu (left in Persian),
  full screen on a phone and not restored open there, closing does not abort a reply, 30 conversations listed,
  context ("this app") only on the app and database main pages.
* **Limits.** 8 model calls per message (an unfinished turn's steps wait on a card and never auto-run), 4,000
  characters per message, last 20 messages sent, provider error text shown verbatim only to platform managers.

## Tests

`AssistantGuideTests` (new, `tests/Harbora.Tests/AssistantGuideTests.cs`, 3 tests):

1. `/features/assistant` maps to `08-ai`; `/features/admin` and `/features/functions` stay unmapped.
2. Chapter 8 renders an `<h2>` «دستیار هوشمند پنل», its two sub-headings, exactly two `<table>`s, and no leaked
   pipe syntax (Markdig shows a malformed table as a paragraph of pipes).
3. The guide's figures are the code's: `AgentSettingKeys.DefaultDailyRequestLimit`,
   `AgentConversationService.MaxModelCallsPerMessage/MaxMessageLength/HistoryMessages/ConversationListSize`,
   `AgentPlan.Lifetime`, `AgentModelBench.Cases.Count` (6) and `ToolSpecs.Count` (9).

Mutation check (all applied at once, test project rebuilt, then `git checkout`): the default limit `۳۰۰` changed
to `۵۰۰` in the chapter, the first table's separator row blanked, and the HelpMap line deleted made **all three
tests fail**, each for its own mutation. Reverted before the final build.

The pre-existing classes that pin tutorial content / HelpMap (`LearningCensusTests`, `LearningLibraryTests`,
`LearnHttpTests`, `LearningCentreDockerImageTests`) plus the new class: `Passed! - Failed: 0, Passed: 30`.

The rendered output of the new section was dumped once (a throwaway test, not committed) and read through:
headings, the nested bullet list inside step 6, both tables and the blockquotes come out as intended.

### Commands and output

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0, 28 Warning(s) (the baseline's package warnings; no CS/RZ/CA), 0 Error(s)
dotnet test  Harbora.slnx --nologo -v q --no-build                       -> EXIT=0
Skipped! - Failed: 0, Passed:    0, Skipped: 108, Total:  108 - Harbora.Postgres.Tests.dll
Passed!  - Failed: 0, Passed:  500, Skipped:  17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:   0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 6831, Skipped:   0, Total: 6831 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" /tmp/test-r6-assistant-docs.log -> 2 ; grep -c MSB3030 -> 0
```

`Harbora.Tests` 6831 = master's 6828 + these 3. (A first build of the new test file had one CS8604 warning; it
was fixed before the commit, and the final build shows none.) The Postgres lane is skipped here as everywhere
(no Docker); nothing in this change touches the database.

## Not verified

* The guide was never compared with a running panel (none can run here): label wording is from the views and
  Vue sources, and screenshots are absent by design.
* Ctrl+J in desktop Chrome/Firefox (where it is also "Downloads") is E's unverified item; the guide tells the
  reader to use the button if the browser keeps the key.
* "03:30 Tehran" for 00:00 UTC assumes Iran stays on a fixed +03:30 (no daylight saving since 2022).

## Concerns

1. **The write-tool statements come from F's and G's briefs, not from code.** On this branch only the six read
   tools exist. The guide's list of what the assistant can propose (create project/environment/database/app,
   deploy template, env vars, domains, volumes, deploy, restart, stop, rollback, attach/detach, delete), the
   examples (create database and restart = safe, stop = dangerous, delete database = destructive with the name
   typed), "the card says what is lost", "queued, not done" for long work, and the limits "no resize/replicas,
   no private Git repos" must be re-read against F and G when they land. The READMEs' "turns every change into a
   plan" has the same dependency.
2. **No screen undoes «دیگر نپرس».** `GET /assistant/approvals` and `POST /assistant/approvals/{tool}/revoke`
   exist (D) but E's pane has no UI for them; the guide says so and names the endpoints. Worth a follow-up.
3. **Design vs. code on who has the feature.** The design doc says "in practice only the provider workspace has
   it"; the code ships `assistant` Locked for every workspace, the owner's included, so the owner has to enable
   their own workspace in Platform → Features. The guide describes the code.
4. **The AI-gateway chapter has a product gap behind fix 2:** nothing in the panel can subscribe a workspace to an
   AI plan, and the user's AI page (`Views/Ai/Index.cshtml`) still tells people to "ask your platform
   administrator to assign a plan". There is also a third inaccuracy I left alone, per "do not rewrite beyond the
   two fixes": step 1 of the gateway walkthrough says the provider's API key is entered in the provider form,
   while the page has a separate credential form per provider (`AiAdminController`, `providers/{id}/credentials`).
5. **`HelpMap` cannot point at a page section.** The assistant settings are a section of `/settings`, which stays
   on chapter 9 (that chapter already links to 8). Only `/features/assistant` was added; `/features/admin` is
   deliberately unmapped because it is the console for every feature.
6. **One addition beyond docs and HelpMap:** `AssistantGuideTests.cs`. It is a new file (no merge conflict is
   possible) and can be dropped without touching anything else, but it keeps the quoted numbers from drifting.
7. The Persian copy is mine and has had no native editor beyond matching the existing chapters' voice; the owner
   may want a read-through of the consent table and the "after Run" list in particular.

---

# Update after everything landed on master (5737b07)

The coordinator asked for the guide to describe what was actually built. The branch was rebased onto master
`5737b07` (clean; the coordinator's `e919153`, the entitlement correction, kept: the provider's default plan has
the assistant at once, customer plans are Locked until granted). Sources read: `f-report.md`, `g-report.md`,
`u-brief.md` (section S), `u-c-report.md`, `u-d-report.md`, `u-e-report.md`, plus the code and the Vue sources
for every label I quote.

Commits (owner identity, no trailer; hashes are after the rebase):

```
08aa498 Assistant guide brought up to what was built: the write tools by kind and where they refuse, the lock button ...
93244c3 READMEs: the assistant paragraph updated for the write tools, streaming and the lock button ...
```

plus the commit adding this section. The earlier commits of this branch were rebased: `98c27e0` is now
`d865dbd`, `c1c397f` is `cb287e9`, `92d3fa3` is `936785a`, `e6846d6` is `c38e2bf`.

## What changed in `docs/tutorial/08-ai.md`

* **Write tools, as built.** A table of every tool by kind. Safe: create project, add environment, create
  database, create app, deploy template, set env vars, add domain, add volume, deploy, restart, attach database,
  start database. Dangerous: delete env var, remove domain, stop app, rollback, detach database, rotate database
  password, stop database. Typed delete: delete app, remove volume, delete database, delete project. The kinds
  and names are F's and G's tables, cross-checked against the tool names registered in the code.
* **Notable refusals**, one bullet each: a taken slug is refused, not suffixed with `-2` (F: create_app and
  deploy_template; G: create_project and add_environment); a Persian-only name needs a Latin slug; a rollback
  must name its deployment number and is refused while one is in flight; restart or stop of a never-deployed
  app, stop of a stopped app, stop of a non-running database and start of a provisioning one are refused;
  attach across environments is refused; data is kept unless asked (`deleteData` and `removeVolumes` default to
  false, and a protected volume is never deleted with its data); a template always creates a new project; no
  private Git through chat (no token, no credentials in the URL); no resize or replicas; a protected
  environment waits for approval.
* **The typed delete** now says what the card says about data (kept unless you said otherwise), that it names
  apps still using the database, and what to type: the resource's name for a database or project, the app's slug
  for deleting an app or a volume (F's remove_volume phrase is the app's slug).
* **Money:** the first-hour warning on app and database cards, and that a payment refusal can still arrive when
  the step runs.
* **New subsection «رمزها: دکمهٔ قفل».** The lock button beside the composer (name, masked value with show/hide,
  «افزودن به پیام», `[🔒 NAME]` in the draft); what the model sees (only `{{secret:...}}`); the value reaches the
  panel in a separate request; held encrypted in memory for **30 minutes**, per person and workspace, at most
  **50** per person (the oldest is forgotten), name up to **100** and value up to **8000** characters; forgotten
  on a panel restart, after which a waiting plan is refused at «اجرا» (the re-check) with "enter it again", so
  nothing runs; chips read «سکرت» after a reload; `generate` for secrets nobody needs to type (48 random
  characters made on the server); the plain-text warning (what it matches, that the first Send is held, that it
  is gentle and incomplete); a plain value for a secret variable is refused; a secret cannot be turned plain
  through the assistant; and a template variable the template keeps plain cannot take a lock-button value
  (deploy with a plain value, then set it with the lock button afterwards).
* **New subsection «جواب، رفتن و برگشتن».** Streaming; the three-dot indicator (also after a tool finishes);
  leaving, refreshing or closing mid-answer (the turn runs on, is saved, the pane re-reads about every 2.5 s);
  the conversation created before the first message and the words returned to the box if the message never
  arrived; a second message while one is being answered waits (typing works, Send does not); the **3 minute**
  turn limit and the restart message.
* **Limits:** 90 s per model request and 45 s of silence; the "what reaches the provider" line says lock-button
  values do not; the "never" list's secret bullet is rewritten; the drawer's history line says an empty
  conversation is not listed.
* **The honesty note** under «تست مدل عامل», and both README paragraphs, no longer say nothing ever met a real
  model. `progress.md` records that the owner ran the agent-model test on production with a free OpenRouter model
  (6/6) and that the read-only assistant answered live. They now say that, and that the write tools, streamed
  answers and the lock button have only run against scripted fakes.

## Tests

`AssistantGuideTests` now pins, from the code: the secret lifetime (`AgentSecretHandles.Lifetime`), the
per-person cap (`InMemoryAgentSecretVault.MaxPerPerson`), the name and value sizes
(`AgentSecretService.MaxNameLength` and `MaxValueLength`) and the turn limit
(`AgentConversationService.DefaultTurnDeadline`), on top of the earlier daily default, call ceiling, plan
lifetime, message, history and list sizes and the bench's 6 prompts and 9 tools. It also asserts the two new
sub-headings and **three** tables now (tools by kind, result words, consent), and that no pipe syntax leaks. The
render test is renamed `..._with_all_three_of_its_tables`.

Mutation check (doc-only, no rebuild): the secret lifetime changed from 30 to 45, the turn limit from 3 to 5, and
the tools table's separator row blanked made `The_guides_figures...` and `...renders_the_assistant_guide...`
fail (2 of 3; the HelpMap test is unaffected, as it should be). Restored with `git checkout`.

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0, 28 Warning(s) (package warnings), 0 CS/RZ/CA warnings, 0 Error(s)
guide + HelpMap + learning classes (AssistantGuideTests, LearningCensusTests, LearningLibraryTests, LearnHttpTests, LearningCentreDockerImageTests) -> Passed! 30/30
dotnet test  Harbora.slnx --nologo -v q --no-build                       -> EXIT=0
Skipped! - Failed: 0, Passed:    0, Skipped: 109, Total:  109 - Harbora.Postgres.Tests.dll
Passed!  - Failed: 0, Passed:  500, Skipped:  17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:   0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 7304, Skipped:   0, Total: 7304 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" /tmp/test-r6-assistant-docs.log -> 2 ; grep -c MSB3030 -> 0
```

`Harbora.Tests` 7304 = F's report figure for master (7301) + this branch's 3; I did not run master separately.

## Not verified / concerns

1. **Nothing here ran against a real model, Docker or a browser.** The guide is written from the reports and the
   code; label wording is from the Vue and Razor sources. The sentence "the first read-only release ran on a real
   free model, 6/6" comes from the coordinator's ledger (`progress.md`), not from anything I saw. **Re-word the
   guide's note and both README paragraphs after the write tools are deployed and tried**, since they say those
   parts have not met a real provider.
2. **Very short secrets are not masked on the way back.** D's scrub replaces held values of 4 characters or more
   (the floor `AssistantRedaction` uses). The lock button accepts shorter ones; the model still only receives
   the handle, but a tool that echoed a 1 to 3 character value would not be scrubbed. The guide says the model
   never sees the value; that holds for what the pane sends, not for such an echo (no tool echoes values today).
3. **A process that dies mid-answer** leaves the conversation "answering" for up to 3.5 minutes (deadline plus
   grace), and a message sent then is refused. The guide only says what the person sees when the panel restarts
   cleanly (the answer stops with a message). This is D's concern 2.
4. **The guide does not describe `deploy_template`'s stricter access** (a project-scoped member is refused,
   because a template always creates a new project) or the audit entries the tools write; both are in F's report.
5. Still true from before: no screen undoes «دیگر نپرس» (API only), and the Persian copy has had no native editor.
