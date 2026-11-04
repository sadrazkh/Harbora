# Launch — "here is my code, make it work"

Owner, 2026-11-04: «بزرگترین مشکل اینه که سازگاری و یادگرفتن کار کردن با این پروژه برای مشتری سخته … باید
یجوری باشه شبیه یه فرم پر کنه اپشو بده … طرف اپ رو نوشته فقط بزنه دیپلوی بشه دیتابیس ساخته بشه کانکت بشه
تست بشه». And: «نیازی به ai لزوما نیست اگر میشه بدون ai هرجایی رو برد جلو بهترم هست».

So: **no model anywhere in this path.** Everything below is deterministic, works with no provider
configured, and costs nothing per use.

## The experience

1. `/launch` asks one question: where is the code? A Git URL, or a zip. (Ready-made apps stay where they
   are, one click away, under Templates.)
2. Harbora fetches it and reads it. No build, nothing created, nothing charged.
3. One confirm screen: *this is what I found, this is what will be created, this is what it costs, and
   here are the only things I need from you.* Everything is editable; almost nothing is required.
4. One button. A timeline then walks: source → databases → app → deploy → checks.
5. It ends on the app's address and the list of what was actually checked — or on the exact step that
   broke and what to do about it.

## What already exists and must not be rebuilt

| Need | Existing thing |
|---|---|
| Create an app | `IAppProvisioningService.CreateAsync(AppCreateRequest)` — one path, form and agent share it |
| Create a database | `IDatabaseProvisioningService.CreateAsync(DatabaseCreateRequest)` |
| Attach | `IDatabaseAttachmentService.AttachAsync`; the variables are computed at deploy by `ManagedServiceAttachEnv.EntriesFor` |
| Set variables | `IAppConfigurationService.SetEnvAsync` |
| Deploy | `IDeploymentEngine.QueueDeploymentAsync`, including `SourceArchivePath` for an upload |
| Clone | `IGitService.CheckoutAsync` |
| Unpack an upload safely | `SourceArchive` (tar.gz today; gains zip here) |
| "Does this database answer, from the network the app is on, with these credentials?" | `IManagedServiceEngine.TestConnectionAsync` — it runs the engine's own client as a one-off on the environment network |
| "Does this address work from outside?" | `IUptimeProbe.ProbeAsync` |
| "Has it stayed up?" | `IDockerEngine.GetLifecycleAsync`, `GetLogsAsync` |
| Choose server and size, with prices | `SizePicker` view component |
| What a price is | `ResourceCreationBilling` (a quote must read this, not a second copy) |
| Stack detection order | `Buildpacks.Detect` — the inspector must agree with it |
| Live deploy log | `/deployments/{id}` |

## The pieces

**The inspector** (`Harbora.Shared.Inspection.SourceInspector`) is pure over a directory, lives in
`Harbora.Shared` so the CLI can run it too, and returns a `SourceInspection`: stack, web or worker, port,
root directory, services needed with their evidence, every variable in the example env file classified,
and notes. It proposes; it never decides.

**The launch** (`AppLaunch`, `ILaunchService`) is a row, so a reload or a restart loses nothing.

```
POST /launch ──> Inspecting ──(job: LaunchInspect)──> AwaitingConfirmation ──> InspectFailed
                                     │ confirm (on the request: each create judges the signed-in user)
                                     ▼
                              Creating ──> WaitingForServices ──> Deploying ──> Verifying
                                     │              │                  │             ├─> Succeeded
                                     └──────────────┴──────────────────┴──> Failed   └─> SucceededWithProblems
```

**Only the inspection is a job.** Everything after `Creating` is moved by a background tick
(`ILaunchService.AdvanceAllAsync`, a few seconds apart, no session). A job that waited for a provision or a
deployment would occupy the worker that work needs, and with `Jobs:MaxConcurrency=1` it would wait for ever.

**The database must answer before the app deploys.** The pipeline does not wait for attached services, and
`TemplateDeploymentService` relies on the queue being first-in-first-out, which stopped being a guarantee of
*finished* when the queue became concurrent. The launch waits on `ManagedService.Status` itself.

**One server.** Everything in a launch lands on one host, because an app and its database must share one
(README, Known limitations). The first placement decides; the rest are passed that `ServerId`.

**The names the app really reads.** Attach already sets `DATABASE_URL`, `PGHOST`, `REDIS_URL` and friends. An
example env file that says `MONGO_URI` or `DB_HOST` gets an `AttachEnvAlias` on the attachment
(`AppManagedService.EnvAliasesJson`), computed with the other attach variables rather than copied, so a
password rotation reaches it too.

**Secrets.** `JWT_SECRET`, `SESSION_SECRET` and their like are generated. A third party's key
(`STRIPE_SECRET_KEY`, `TELEGRAM_BOT_TOKEN`) is the one thing only a person can answer, and the one thing the
screen asks. An example file's value is never trusted as a secret.

**The checks** (`IAppVerifier`) answer what a green deployment does not:

| Key | Question | How |
|---|---|---|
| `running` | Is it still running? | the container's state |
| `restarts` | Did it stay up? | `GetLifecycleAsync` over a window after the cutover |
| `service:{id}` | Does each attached database answer its credentials from the app's network? | `TestConnectionAsync`; on failure connect the service to the environment network (additive, never a restart) and ask once more |
| `url` | Does the address work from outside? | `IUptimeProbe`, Pending while a certificate is issued, with the same "root may 404" rule as `HealthProbeRule` |
| `logs` | Is the app itself complaining? | the log tail against known failures — `localhost:5432`, `ECONNREFUSED`, `password authentication failed`, a server bound to `127.0.0.1` — each with a sentence saying what it means |

A check that cannot run comes back `Skipped` with the reason, never as a pass. It is also a button on every
app's page, so this is not only for launches.

## Not in this design

- **No model.** Not behind a flag; there is no code path that calls one.
- **Silent creation.** A guess is shown before anything exists, along with what it costs.
- **Scale to zero, a new build system, a new way of making an app.** None of it is needed.
- **The 19-field form.** Left alone apart from a link to `/launch`. Reading it showed it already reveals only
  the fields a chosen source needs, so regrouping it was a solution to a problem it does not have.
