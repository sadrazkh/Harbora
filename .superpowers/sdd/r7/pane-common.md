# Pane — rules shared by P1, P2 and P3 (binding)

Three agents change the assistant pane at the same time, in separate worktrees, and their branches are
merged one after another. These rules keep the merges clean and the pieces fitting.

Read first: `.superpowers/sdd/r7/look.md` (the look the owner approved) and the header comment of
`src/Harbora.Web/Scripts/islands/AssistantPanel.vue` (rules live in plain modules tested by `node --test`;
the `.vue` files wire them to markup). Model text is untrusted: never `v-html`, never build HTML strings —
`assistantFormat.js` explains why.

## Who owns what

| File / region | Owner |
|---|---|
| `AssistantPanel.vue`: the History view (`v-if="view === 'history'"` block and its script: `showHistory`, `openConversation`, `conversations*`) — move it into a new `AssistantHistory.vue` | **P1** |
| `AssistantPanel.vue`: the conversation log (from `data-assistant-log` to the `data-assistant-thinking-status` line) and the tool-line helpers (`toolText`, `toolIcon`, `toolIconClass`) | **P2** |
| `AssistantPanel.vue`: the header buttons and the composer `<form>` | **P3** |
| `assistantTimeline.js`, `assistantStream.js`, new `assistantActivity.js`, `AssistantPlanCard.vue`, `AssistantResultCard.vue` | **P2** |
| `assistantFormat.js`, `AssistantText.vue`, `assistantDrawer.js`, the drawer markup in `Views/Shared/_Layout.cshtml` | **P3** |
| new `assistantHistory.js` | **P1** |
| `assistantApi.js` | P1 adds `conversations({ q, skip })`, `deleteConversation(id)`, `deleteAllConversations()`; P3 adds `stop(id)`. Each touches only its own functions. |
| `assistantSession.js` | P1 may add `forget(id)`; P3 adds `stop()`. Each touches only its own functions. |
| `app.css` | anyone; append only, in a section headed by a comment naming the task |
| `Scripts/tests/` | new test files named after your module; do not edit another task's tests |

If you find you must change something another task owns, do the smallest thing, and name it in your
report under "touched outside my area" so the merge can be checked.

## Shapes all three use

- **Server** (master `44d38d0`, `src/Harbora.Infrastructure/Agent/AgentViews.cs` and `AgentCoreModels.cs`):
  `GET /assistant/conversations?q=&skip=` (30 a page; shorter page = last);
  `DELETE /assistant/conversations/{id}` → 204, 404 `not_found`, 409 `busy`;
  `DELETE /assistant/conversations` → `{ deleted, skipped }`;
  `POST /assistant/conversations/{id}/stop` → `{ stopped }`;
  stream event `stopped` `{ messageEn, messageFa }` (followed by `done`);
  tool events gain `labelEn, labelFa, category, durationMs`;
  stored tool messages gain `toolLabel, toolCategory, durationMs, planId`;
  the conversation view gains `plans: [{ plan, result, afterMessageId }]`.
  The server side is being built in parallel (S1, S2); code and test against these shapes with fakes.
- **API errors**: `assistantApi.js` already turns a refusal into an error carrying `code`, `messageEn`,
  `messageFa`; reuse that for 404/409 on delete.
- **Timeline items** (P2 defines them in `assistantTimeline.js`; P1 and P3 only read them):
  `tool` gains `labelEn, labelFa, category, durationMs`; `tool-history` gains `label, category,
  durationMs, planId`; a new kind `stopped` `{ messageEn, messageFa }`; decided plans and their results
  from `plans` become ordinary `plan` and `result` items in place.
- **Category → icon** (P2 owns the map; lucide icons): app → `AppWindow`, deployment → `Rocket`,
  database → `Database`, project → `Folder`, domain → `Globe`, volume → `HardDrive`, env → `KeyRound`,
  catalog → `LayoutGrid`, usage → `Gauge`, resources → `Boxes`, anything else → `Wrench`.

## Quality bar

- Both languages for every visible word (`t(en, fa)`), RTL-correct (logical classes), keyboard reachable,
  visible focus, `aria-*` where the current pane uses them, `prefers-reduced-motion` respected for any
  new motion.
- Phone width: the drawer is full-width there; nothing you add may scroll sideways at 360 px.
- Every rule in a plain module with `node --test` tests; markup only wires. Keep the existing 230 node
  tests green; if an existing test pinned markup you changed on purpose, update it and say so.
- `npm run build` exits 0. Then the full dotnet suite once at the end (some HTTP tests pin the layout).
