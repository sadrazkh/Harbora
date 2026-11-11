# G: the CLI and the API can act in a team workspace

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first, then `docs/product-audit/19-do-not-change-list.md` (items 16,
25 and 27 in particular) and `docs/cli-deploy.md`.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-g`, branch `r9/g`. Report: `.superpowers/sdd/r9/g-report.md`.

## What the code does today

- A token belongs to a user and to no workspace (`src/Harbora.Domain/Identity/ApiToken.cs`).
- `src/Harbora.Web/Infrastructure/TokenAuthenticationHandler.cs:38-52` builds the caller from the user's
  memberships ordered by "personal first", with no tie-break, and takes the first.
- So `harbora deploy`, `harbora launch`, `harbora apps` and `harbora env pull` always act in the person's
  own workspace. An app in a team workspace cannot be reached from the CLI or from CI at all.
- Every `api/v1` controller signs in through this handler (`Controllers/Api/ApiV1Controller.cs`,
  `LaunchApiController.cs`, and the nodes, backup and sync APIs).
- A browser session can switch workspace (`WorkspacesController`, `SwitchSessionAsync`) and is re-checked
  on every request by `WorkspaceMembershipValidationMiddleware`. Find out whether that middleware covers a
  token caller as well.

## The goal

A person, and a CI job, can say which workspace a CLI or API call acts in. Saying nothing behaves as it
does today, because installed CLIs are older than the panel they talk to; but make today's choice
deterministic.

## What must hold

This is the boundary between tenants.

- A named workspace is honoured only when the token's user is a member of it now, checked on every
  request, and the caller gets the role of that membership, not of another.
- Archived, deleted and suspended workspaces are treated as the browser path treats them.
- A workspace that cannot be used is a refusal in a sentence that names what was asked for. It is never a
  quiet fall back to the personal workspace: that deploys to the wrong place and reports success.
- A workspace the caller is not in and one that does not exist answer the same way.
- A new CLI talking to a panel too old to understand the choice must notice and refuse, for the same
  reason. The CLI already compares versions; use whatever makes the check certain.
- Before a command does something that costs money or changes an app, it says which workspace it is about
  to act in.

## Shape

Yours to decide. One that needs no schema change: the workspace travels with each request; an endpoint
lists the caller's workspaces; `whoami` reports the active one; the CLI can list workspaces and choose one
per signed-in account (`HarboraConfig.cs`), per project (`harbora.yml`, where unknown keys must keep being
tolerated: `ProjectConfig.cs`) and per command, with a precedence that is printed the way `DeployPlan.Decide`
prints its reason.

Pinning a token itself to one workspace would suit CI well and needs a column. There is no migration this
round: if you think it is the right next step, describe it in the report.

## Tests

`ApiV1ContractHttpTests`, `CrossTenantIsolationTests` and `WorkspaceQueryFilterTests` show the idioms. The
cases that matter most are the refusals: not a member, removed between two requests, archived, unknown,
and an old panel.

## Owned and not

Yours: `TokenAuthenticationHandler.cs` and what it builds on, `Controllers/Api/ApiV1Controller.cs`, the CLI's
`ApiClient.cs`, `HarboraConfig.cs`, `ProjectConfig.cs`, `Commands.cs`, `Program.cs`, the one line in
`LaunchCommand.cs` and `LaunchPlan.cs` that names the workspace, and one section of your own in
`docs/cli-deploy.md` and the Persian `docs/tutorial/12-cli-deploy.md`.

Not yours: `SourcePacker.cs` and the env checks in `Doctor.cs` (A, who is also adding a CI section to the
same two guides: add yours without reflowing theirs); `Launch/*` on the server (C).
