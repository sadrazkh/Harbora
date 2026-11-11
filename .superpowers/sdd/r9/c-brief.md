# C: pick a repository from a list, have pushes deploy without pasting anything, and keep answers on retry

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first, then `docs/superpowers/specs/2026-11-04-launch-design.md`
and `docs/tutorial/15-launch.md`.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-c`, branch `r9/c`. Report: `.superpowers/sdd/r9/c-report.md`.

The owner's complaint behind Launch: learning the product is too hard, and a person who wrote an app should
press one button. Three places still ask for more than that. Commit each item on its own.

## What the code does today

- `/launch` takes an address and an optional token (`src/Harbora.Infrastructure/Launch/LaunchService.cs:82-137`,
  `Launch/LaunchSource.cs`). A person who has already connected GitHub, GitLab or Gitea still pastes.
- The app a launch creates gets a private `GitProvider` of type `Custom` holding that token
  (`Apps/AppProvisioningService.cs:132-140`). It is never tied to the connected account.
- So for pushes to deploy, the person copies a URL and a secret from the app page into the provider's
  settings by hand (`src/Harbora.Web/Views/Apps/Details.cshtml:936-945`). The receiving side exists:
  `WebhooksController` (`webhooks/git/{repositoryId}`), `Git/GitWebhookProcessor.cs`, `GitRepository.WebhookSecret`.
- The provider client can list repositories and nothing else
  (`src/Harbora.Application/Abstractions/IGitProviderClient.cs:11`, `Git/GitProviderClient.cs`). Connecting an
  account: `Controllers/GitController.cs`, `Views/Git/Index.cshtml`, `Git/GitOAuthService.cs`.
- A retried launch has lost what the person typed on the confirm screen: `Launch/LaunchService.Confirm.cs:81-129`
  passes an empty dictionary, and the comment there points at `LaunchJson.WriteChoices` for why.

## 1. Pick a repository from a list

On `/launch`, a person with a connected account chooses a repository from their own list instead of pasting
an address. Pasting stays, and stays the whole flow for someone with nothing connected. It works with no
script (Persian by default, RTL), and a long list is usable. When the list cannot be fetched (expired
token, provider down), that is one sentence and the paste field still works.

## 2. Pushes deploy with nothing to paste

When an app's repository belongs to a connected account whose credential may manage hooks, Harbora creates
the webhook itself through the provider's API, with the URL and secret it already has, and the app page says
pushes deploy automatically instead of asking for a paste. What must hold:

- Once only: a launch retry, a re-save or a second app on the same repository finds the existing hook
  rather than adding another.
- When it could not (a `Custom` provider, no permission, the API refused), the page keeps today's manual
  instructions and says why in a sentence. It never claims a hook it did not confirm.
- A launch that came through the picker uses the connected account, not a new `Custom` provider. A pasted
  address that plainly belongs to a connected account should do the same; read how the ordinary create
  form resolves a repository before deciding how far to take this.
- The ordinary create form benefits too if it shares the code path.
- Removing the hook when the app or repository goes: do it if there is a natural place, otherwise report.

Real provider APIs cannot be reached from here. Write against each provider's documented API, test with
fake HTTP handlers the way the client is tested today, and list in the report exactly which calls have
never met a real provider. Do not describe them as working.

## 3. A retry keeps what was typed

Find out why answers were not kept before changing it: typed values can be secrets, and a launch row is
not a place for a secret in plain text. The launch already stores its Git token protected
(`EncryptedGitToken`); and once the app exists, its variables are already written. Choose the smallest
honest design. The sentence on the failed page that says answers are lost goes when it stops being true.
`AppLaunch` has text columns already; if you conclude a new column is unavoidable, stop and say so.

## Owned and not

Yours: `Launch/*`, `Git/*`, `IGitProviderClient`, the Git part of `Apps/AppProvisioningService.cs`,
`Controllers/LaunchController.cs`, `Controllers/Api/LaunchApiController.cs`, `Controllers/GitController.cs`,
`Views/Launch/*`, `Scripts/launch.js` and its tests, the webhook block in `Views/Apps/Details.cshtml` and
nothing else in that file, `docs/tutorial/15-launch.md` (held to the code by `LaunchChapterTests`) and the
README sentences about launch that your change makes untrue.

Not yours: `Deployments/SourceArchive.cs` and the upload filter (A); database services (B); the
verification card and rollback on the app page (E); `Verification/*` (E); `src/Harbora.Cli/*` (A and G).
Agent B gives new databases a backup schedule inside the provisioning service; you need do nothing for it.
