# H — The assistant in the learning center (wave B, docs only)

The in-panel AI assistant is new (design: `docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`;
what was built: `.superpowers/sdd/r6/c-report.md`, `d-report.md`, `e-report.md`). The owner and, later,
customers need a Persian guide. The learning center renders `docs/tutorial/*.md`
(`src/Harbora.Infrastructure/Learning/LearningLibrary.cs`, `HelpMap.cs`); AI lives in
`docs/tutorial/08-ai.md`, which is Persian.

## Write

1. In `docs/tutorial/08-ai.md`, a new section «دستیار هوشمند پنل» (keep the existing gateway section; do not
   rewrite it beyond the two fixes in item 3). Plain Persian, short paragraphs and lists, in the voice of
   the existing tutorial pages:
   - what it does, with three or four example requests in Persian and one in English;
   - setup for the platform owner: Settings → assistant: provider «OpenAI-compatible», base URL
     `https://openrouter.ai/api`, the OpenRouter key (entered by the owner), the agent model and fallback
     models (ids look like `vendor/model:free`), the daily request limit (blank = 300, 0 = unlimited), and
     the «آزمون مدل» button — what its table means, and that it spends about six requests;
   - turning it on for a workspace: the `assistant` feature under Platform → Features (Locked by default);
   - the three consent levels exactly as built: safe changes ask once and offer «دیگر برای این کار نپرس»;
     dangerous changes always ask; deletes ask you to type the resource's name — with one example each;
   - what it never does: it has your permissions and no more; it never sees secret values; nothing changes
     until you press Run (or you said "don't ask again" for that kind of safe change); a new message
     cancels a plan you did not run;
   - the drawer: the toggle in the top bar, Ctrl+J, it opens on the side opposite the menu;
   - limits: requests per day, at most 8 model calls per message, free models' quality varies — use the
     test button to pick one.
2. `HelpMap.cs`: map the settings page's assistant section and any route the pane links to, if the map
   works by route and such routes exist (check how it works; do not invent routes).
3. Two inaccuracies an earlier review found in the same file: it lists OpenAI and local-model provider
   types and an `http://localhost:11434` base URL **for the gateway** (the gateway accepts only
   OpenRouter/OpenAI-compatible over HTTPS, non-private hosts — local URLs are true only for the
   assistant), and it says a gateway plan can be assigned "from Tenants or this page" (no such code). Fix
   both sentences to say what the product does.
4. `README.fa.md` and `README.md`: one short paragraph each about the assistant, near the existing AI
   mention.

Screenshots are not part of this task (the panel cannot run on this machine). Do not add image links that
do not exist.

## Check

Any test that pins tutorial content or `HelpMap` (search `tests/` for `08-ai`, `HelpMap`, `LearningLibrary`)
must pass; run those test classes, then the full suite once (CONSTRAINTS recipe).

## Report

`.superpowers/sdd/r6/h-report.md` (use `git add -f`), committed; return status, commits, one-line test
summary, concerns.
