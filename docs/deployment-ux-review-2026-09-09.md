# Deployment UX review — 2026-09-09

## Existing capabilities reviewed

Harbora already exposes Git/buildpack, Dockerfile, container image, Compose, static site and CLI deployment paths; ready templates; project environments; placement and instance sizing; release commands; previews; deployment progress and logs. Adding another deployment engine is not necessary to address the friction found in these screens.

## Implemented

- Expose service kind and container port before advanced settings, with binding guidance.
- Open scheduled-job settings when Cron is selected.
- Reflect name, environment, kind, port and create-only intent in the deployment summary.
- Require the relevant image/repository field in the browser, retaining server validation.
- Open collapsed details when native validation needs to focus an invalid field.
- Add bilingual log search, error filtering, downloaded logs, pause/follow controls and empty/error states.
- Keep live socket delivery; reconcile provisional lines with persisted sequence numbers, rejoin after reconnect, and independently recover through serialized polling.
- Abort requests and remove timers on unmount. Perform bounded trailing reads after a terminal status.

## Compatibility and validation

No schema, API, stored setting, package dependency or deployment-engine changes. Existing apps and deployments are not modified. Nothing was published to the running installation.

The frontend production build passes. The existing Harbora.Tests suite passed 6,155 tests. New Node log-behavior tests were added, but their execution was blocked by the sandbox and the escalation request was declined; these tests are not claimed as passing. Browser visual verification and a real container deployment have not been performed.

## Remaining review priorities

- Verify deployment hub subscriptions enforce the same workspace/app visibility as the HTTP logs endpoint; the inspected Subscribe method currently joins a supplied group without that resource-level check.
- Validate the full create/deploy/recover journey in Persian RTL and English on a staging installation, including private repositories, Compose and worker workloads.
- Consider sequence identifiers in socket events for exact reconciliation under concurrent log delivery; existing socket messages have no sequence field, so the UI matches provisional occurrences by stream and message.
- Review long-running build log volume and endpoint pagination before introducing UI truncation that could hide diagnostic information.

## Reference patterns

- https://docs.railway.com/deployments/reference — deployment stages and access to deployment logs.
- https://docs.railway.com/deployments/healthchecks — readiness before marking a deployment active.
- https://render.com/docs/health-checks — readiness and health-check configuration.

These informed the review; no claim is made that their runtime guarantees were newly implemented in Harbora.
