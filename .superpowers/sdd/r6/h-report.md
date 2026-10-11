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
