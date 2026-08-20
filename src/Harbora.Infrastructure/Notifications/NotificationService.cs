using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Delivers notifications to every matching alert. Channel targets are stored encrypted as JSON;
/// webhook channels go out over HTTP, email over SMTP.
///
/// <para>
/// N1 (2026-08-16 notification-system spec) moved the actual channel attempt off this class's own
/// call stack and onto the job queue: <see cref="NotifyAsync"/> and <see cref="NotifyRuleAsync"/> now
/// write a durable <see cref="NotificationDelivery"/> row and enqueue <see cref="JobKind.NotificationDelivery"/>
/// rather than dispatching inline. <see cref="ExecuteQueuedDeliveryAsync"/> is the job's body —
/// <c>NotificationDeliveryJobHandler</c> is the thin <c>IJobHandler</c> adapter that calls it — and it
/// is what makes a channel refusal retried three times with backoff instead of lost the moment it
/// happens. <see cref="SendTestAsync"/> is unchanged: the panel's Test button wants the server's own
/// words immediately, not a queued verdict.
/// </para>
///
/// <para>
/// N4 ("in the reader's own language"): <see cref="NotifyAsync"/>/<see cref="NotifyRuleAsync"/> no
/// longer take a pre-built title/body — a raise site hands over a
/// <see cref="NotificationEventData"/>, and this class is where rendering happens, via
/// <see cref="catalog"/>. The in-app copy (<see cref="FanOutToMembersAsync"/>) is rendered once per
/// member, in that member's own <c>User.PreferredCulture</c> — the same event genuinely reads
/// differently for two people in the same workspace. A channel delivery (Telegram, Discord, a
/// webhook, or an alert's own email target) has no person attached to it, so it renders once, in the
/// platform's default culture (<see cref="NotificationTemplateCatalog.DefaultCulture"/>) — the same
/// default <c>User.PreferredCulture</c> already documents. The admin-fallback path
/// (<see cref="EnqueueFallbackToAdminsAsync"/>) sits between the two: it names a real person by email,
/// so it renders per admin, the same as the member fan-out.
/// </para>
///
/// <para>
/// N5 ("noise control"): <see cref="FanOutToMembersAsync"/> is also where a member's own
/// <c>NotificationPreference</c> and quiet hours are resolved — every earlier stage of this class
/// still writes the same rows it always did, and N5 changes only whether/where a given member's copy
/// actually lands. A critical event is never affected by quiet hours and can only be re-routed, never
/// silenced entirely — see that method's own doc for the invariant it enforces as a second, defensive
/// check on top of <c>NotificationPreferenceService.SetAsync</c>'s own.
/// </para>
/// </summary>
public sealed class NotificationService(
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    PlatformMailer platformMailer,
    IFunctionEventBus functionEvents,
    IServiceScopeFactory scopeFactory,
    ISystemClock clock,
    Microsoft.Extensions.Options.IOptions<NotificationOptions> options,
    INotificationTemplateCatalog catalog,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>
    /// Channel targets are written by the controller with camelCase names ("url", "botToken") and read
    /// back into PascalCase records. System.Text.Json matches case-sensitively by default, so every
    /// field came back null and every channel failed with "not an absolute URL" — for as long as
    /// notifications have existed. Nobody saw it because the failure was swallowed and the Test button
    /// reported success regardless. Reading case-insensitively fixes the targets already stored.
    /// </summary>
    private static readonly JsonSerializerOptions TargetJson = new() { PropertyNameCaseInsensitive = true };
    /// <summary>
    /// <inheritdoc cref="INotificationService.NotifyAsync" path="/summary/para"/>
    ///
    /// <para>
    /// The number counts rules the message was <i>handed to</i>, not rules that took it. A channel
    /// that answered 404 is counted, because it is a rule that exists and its refusal is recorded on
    /// the row itself; zero means the workspace has nowhere for this to go, which is the outcome
    /// that has no other home — nothing throws and no row changes.
    /// </para>
    /// </summary>
    public async Task<int> NotifyAsync(Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
    {
        var alerts = await db.Alerts
            .Where(a => a.WorkspaceId == workspaceId && a.IsEnabled && a.MinSeverity <= severity)
            .ToListAsync(ct);

        var matching = alerts.Where(a => Matches(a, evt.Type)).ToList();

        // N3 ("told a person, not a channel"): the in-app copy is not the zero-rule fallback's
        // understudy below — it is written for every member whether or not any rule matched, and
        // whether or not a channel exists at all. See FanOutToMembersAsync for why. N4: rendered once
        // per member's own culture, not the single string every earlier version of this method built.
        // N5 ("noise control"): the same call now also resolves each member's own preference and
        // quiet hours — see that method's own doc.
        await FanOutToMembersAsync(workspaceId, evt, severity, ct);

        if (matching.Count > 0)
        {
            // Rendered once, in the platform's default culture — a Telegram group or a webhook has no
            // person to read PreferredCulture off, so every matching rule shares the one rendering
            // rather than repeating the same render per rule.
            var (encryptedBody, subject) = RenderForChannel(evt);
            foreach (var alert in matching)
                await EnqueueDeliveryAsync(new NotificationDelivery
                {
                    WorkspaceId = workspaceId,
                    Purpose = NotificationDeliveryPurpose.AlertDispatch,
                    Channel = alert.Channel,
                    AlertId = alert.Id,
                    Severity = severity,
                    Subject = subject,
                    EncryptedBody = encryptedBody
                }, ct);
        }

        // §3 way two: nothing seeds an Alert row, so a workspace that never visited the alerts page
        // matches zero rules here — not because it opted out, but because there was never anything to
        // opt out of. Every rule with sufficient severity that DID opt out of this event is left
        // alone: that is a choice the fallback below must not override, which is why this asks "does
        // any rule exist at all" rather than reusing `matching`.
        if (matching.Count == 0 && evt.Type != AlertEvent.Test
            && !await db.Alerts.AnyAsync(a => a.WorkspaceId == workspaceId, ct))
            await EnqueueFallbackToAdminsAsync(workspaceId, evt, severity, ct);

        // Functions subscribe to the same happenings people do, so they are told from here rather
        // than from a second set of raise-sites kept in step by hand — the arrangement that ends with
        // an operator being emailed about a crash no function was ever told about.
        //
        // Deliberately outside the rule matching above: a workspace with no alert rules still has
        // functions, and making code depend on somebody having configured a notification channel
        // would be an invisible coupling nobody could debug. Rendered at "en" specifically — a
        // function reads event.data.title/body as a stable machine contract, not a message shown to a
        // particular reader, and English is what that contract has always carried.
        if (Domain.Functions.FunctionEvents.ForAlert(evt.Type) is { } functionEventKey)
        {
            var rendered = catalog.Render(evt, "en");
            await functionEvents.PublishAsync(
                Domain.Functions.FunctionEvent.Create(
                    functionEventKey, workspaceId, rendered.Subject,
                    ("title", rendered.Subject), ("body", rendered.TextBody), ("severity", severity.ToString())),
                ct);
        }

        return matching.Count;
    }

    /// <summary>
    /// <inheritdoc cref="INotificationService.NotifyInAppOnlyAsync"/>
    /// </summary>
    public Task NotifyInAppOnlyAsync(
        Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct) =>
        FanOutToMembersAsync(workspaceId, evt, severity, ct);

    /// <summary>
    /// One rule, by id. IgnoreQueryFilters because the caller is a background evaluator with no
    /// session — the workspace filter would find nothing and report a clean pass.
    ///
    /// <para>
    /// Queues, like <see cref="NotifyAsync"/> — the returned <see cref="NotificationResult"/> now
    /// means "handed to the queue", not "delivered": nothing here can know the channel's answer until
    /// <see cref="ExecuteQueuedDeliveryAsync"/> runs. No caller reads <c>.Delivered</c>/<c>.Error</c>
    /// from this method today; the type is kept so the early "no such rule"/"disabled" refusals still
    /// have a way to say why nothing was queued.
    /// </para>
    /// </summary>
    public async Task<NotificationResult> NotifyRuleAsync(
        Guid alertId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
    {
        var alert = await db.Alerts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");
        if (!alert.IsEnabled) return NotificationResult.Failed("That alert rule is disabled.");

        // N3: a per-app threshold breach is a workspace event too, so it gets the same fan-out
        // NotifyAsync gives every other event — see FanOutToMembersAsync.
        await FanOutToMembersAsync(alert.WorkspaceId, evt, severity, ct);

        var (encryptedBody, subject) = RenderForChannel(evt);
        await EnqueueDeliveryAsync(new NotificationDelivery
        {
            WorkspaceId = alert.WorkspaceId,
            Purpose = NotificationDeliveryPurpose.AlertDispatch,
            Channel = alert.Channel,
            AlertId = alert.Id,
            Severity = severity,
            Subject = subject,
            EncryptedBody = encryptedBody
        }, ct);

        return NotificationResult.Ok;
    }

    /// <summary>Renders once, in the platform's default culture, for a destination with no person
    /// attached — see the class doc for why a channel delivery does not ask a member's
    /// <c>PreferredCulture</c> the way the in-app row and the admin fallback do.</summary>
    private (string EncryptedBody, string Subject) RenderForChannel(NotificationEventData evt)
    {
        var rendered = catalog.Render(evt, NotificationTemplateCatalog.DefaultCulture);
        return (protector.Protect(ChannelBody.Encode(rendered.TextBody, rendered.HtmlBody)), rendered.Subject);
    }

    public async Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return NotificationResult.Failed("That alert rule no longer exists.");

        return await DispatchSafe(alert, AlertSeverity.Info, "Harbora test",
            "This is a test notification from Harbora.", ct);
    }

    /// <summary>
    /// Which rules an event goes to.
    ///
    /// <para>
    /// The default is <c>false</c>, which is the safe answer for a rule-matching function and a trap
    /// for whoever appends the next <see cref="AlertEvent"/>: an event with no arm here is delivered
    /// to nobody, raises nothing, throws nothing, and leaves its caller reporting a notification
    /// sent. Anything appended to that enum needs a line in this switch on the same day.
    /// </para>
    ///
    /// <para>
    /// <see cref="AlertEvent.LowBalance"/> answers true for every rule rather than reading an opt-in
    /// flag of its own, and that is deliberate. Its five neighbours are things that happened to one
    /// resource; this one says the whole workspace is about to stop, and it is the last message the
    /// platform sends a customer while they can still do something about it — an install where
    /// somebody had quietly unticked it would deliver silence and a suspension. The customer's own
    /// out is the one every rule already has: switch the rule off, or set its minimum severity above
    /// Warning. Adding a sixth checkbox would also mean a column, a migration and a bilingual label,
    /// which is a lot of surface to build for the answer "no thank you, do not tell me".
    /// </para>
    ///
    /// <para>
    /// <see cref="AlertEvent.ServiceProvisionFailed"/> (P4, 2026-08-17 app-environment-management
    /// design) answers true for the same reason of shape, not the same reason of urgency: it is not
    /// workspace-wide like <see cref="AlertEvent.LowBalance"/>, but P4's own schema budget is one
    /// column — <c>ManagedService.ErrorMessage</c> — and that column is what this sub-project spends
    /// it on. A workspace's <see cref="AlertSeverity"/> floor on each rule (already checked before this
    /// method runs) is the filter a database failure gets; a seventh checkbox is not.
    /// </para>
    /// </summary>
    private static bool Matches(Alert a, AlertEvent evt) => evt switch
    {
        AlertEvent.DeployFailed => a.OnDeployFailed,
        AlertEvent.AppCrashed => a.OnAppCrashed,
        AlertEvent.SslExpiring => a.OnSslExpiring,
        AlertEvent.DiskWarning => a.OnDiskWarning,
        AlertEvent.BackupFailed => a.OnBackupFailed,
        AlertEvent.LowBalance => true,
        AlertEvent.ServiceProvisionFailed => true,
        AlertEvent.Test => true,
        // A platform announcement is never a workspace's own configured Alert channel — it reaches
        // people only through NotifyInAppOnlyAsync's N3 fan-out, not a Telegram group or webhook a
        // customer set up for deploy failures. Falls to the default below anyway; named explicitly
        // because this method's own doc asks every appended event to get a same-day line here.
        AlertEvent.PlatformAnnouncement => false,
        _ => false
    };

    /// <summary>Used only by <see cref="SendTestAsync"/>: one attempt, swallowed into a
    /// <see cref="NotificationResult"/> the panel's Test button can show immediately. Not templated —
    /// see the class doc — so both languages get the identical text/HTML alternative.</summary>
    private async Task<NotificationResult> DispatchSafe(Alert alert, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        NotificationResult result;
        try
        {
            await DispatchOnceAsync(alert.Channel, alert.EncryptedTarget, severity, title,
                new ChannelBody(body, null), ct);
            result = NotificationResult.Ok;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification via {Channel} failed for alert {Id}.", alert.Channel, alert.Id);
            result = NotificationResult.Failed(ex.Message);
        }

        await RecordAttemptAsync(alert, result, ct);
        return result;
    }

    /// <summary>
    /// One channel attempt. Throws rather than swallowing — this is the seam
    /// <see cref="ExecuteQueuedDeliveryAsync"/> runs on the job queue and <see cref="DispatchSafe"/>
    /// (the Test button's synchronous path) both share, so the four channel senders below are written
    /// once. A non-2xx response or the delivery's own timeout budget becomes a
    /// <see cref="NotificationChannelException"/> carrying whether a second attempt is worth trying;
    /// everything else propagates as whatever the sender itself threw.
    /// </summary>
    private async Task DispatchOnceAsync(
        AlertChannel channel, string encryptedTarget, AlertSeverity severity, string title, ChannelBody body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.DeliveryTimeout);

        try
        {
            var target = string.IsNullOrEmpty(encryptedTarget) ? "{}" : protector.Unprotect(encryptedTarget);
            await (channel switch
            {
                AlertChannel.Telegram => SendTelegram(target, title, body.Text, timeout.Token),
                AlertChannel.Discord => SendDiscord(target, severity, title, body.Text, timeout.Token),
                AlertChannel.Webhook => SendWebhook(target, severity, title, body.Text, timeout.Token),
                AlertChannel.Email => SendEmail(target, title, body, timeout.Token),
                _ => Task.CompletedTask
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The delivery's own budget, not the caller's cancellation — a DNS blip or a slow
            // endpoint, which a second attempt can plausibly get past.
            throw new NotificationChannelException(
                $"The channel did not respond within {_options.DeliveryTimeout.TotalSeconds:0.##} seconds.",
                isRetryable: true);
        }
    }

    /// <summary>
    /// Writes the outcome back onto the rule. Best-effort by design: the notification has already been
    /// delivered (or not) by this point, and turning bookkeeping into a second failure helps nobody.
    /// </summary>
    private async Task RecordAttemptAsync(Alert alert, NotificationResult result, CancellationToken ct)
    {
        try
        {
            alert.LastAttemptAt = DateTimeOffset.UtcNow;
            alert.LastError = result.Delivered ? null : Truncate(result.Error);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the delivery outcome for alert {Id}.", alert.Id);
        }
    }

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 400 ? error : error[..400];

    /// <summary>
    /// The job body <c>NotificationDeliveryJobHandler</c> runs — one attempt at one queued
    /// <see cref="NotificationDelivery"/>. Rethrows on failure so
    /// <see cref="Harbora.Infrastructure.Jobs.JobExecutionPolicy"/>'s retry/backoff decides what
    /// happens next; the row itself is updated either way, which is what makes "the attempts are
    /// visible" true regardless of how the job worker judges the exception.
    /// </summary>
    public async Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return; // Nothing to do — the row is gone; re-running finds no work.

        // Idempotent against a re-claim after a crash: a delivery already settled must never be sent
        // twice because the worker's own bookkeeping did not make it to disk in time.
        if (delivery.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.Suppressed)
            return;

        Alert? alert = null;
        if (delivery.Purpose == NotificationDeliveryPurpose.AlertDispatch)
        {
            alert = delivery.AlertId is { } alertId
                ? await db.Alerts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == alertId, ct)
                : null;
            if (alert is null)
            {
                // The rule was deleted between this being queued and the job running. Not a channel
                // refusal — there is no channel any more — so this is Suppressed, not Failed, and
                // never retried.
                delivery.Status = NotificationDeliveryStatus.Suppressed;
                delivery.LastError = "That alert rule no longer exists.";
                delivery.LastAttemptAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // Doc 09 §6: a channel with no configuration degrades to Suppressed with a reason rather than
        // throwing. Checked up front so an unconfigured install does not spend three attempts and
        // thirty-one minutes of backoff finding that out.
        if (UsesPlatformMailer(alert) && !await platformMailer.IsConfiguredAsync(ct))
        {
            delivery.Status = NotificationDeliveryStatus.Suppressed;
            delivery.LastError = "Platform SMTP is not configured.";
            delivery.LastAttemptAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            if (alert is not null)
                await RecordAttemptAsync(alert, NotificationResult.Failed(delivery.LastError), ct);
            return;
        }

        delivery.Attempts++;
        delivery.LastAttemptAt = clock.UtcNow;

        try
        {
            // ChannelBody.Decode recognises N4's own {text, html} envelope and falls back to treating
            // the whole decrypted string as plain text (Html: null) for anything else — every delivery
            // OutboxMail.Queue ever wrote, and every row a restart-surviving job re-claims from before
            // this class knew how to template anything.
            var body = ChannelBody.Decode(protector.Unprotect(delivery.EncryptedBody));
            if (alert is not null)
                await DispatchOnceAsync(alert.Channel, alert.EncryptedTarget, delivery.Severity, delivery.Subject, body, ct);
            else
                await platformMailer.SendAsync(delivery.RecipientAddress!, delivery.Subject, body.Text, body.Html, ct);

            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.LastError = null;
            await db.SaveChangesAsync(ct);

            if (alert is not null) await RecordAttemptAsync(alert, NotificationResult.Ok, ct);
        }
        catch (Exception ex)
        {
            delivery.LastError = Truncate(ex.Message);
            var canRetry = Jobs.JobExecutionPolicy.IsRetryable(ex)
                           && delivery.Attempts < Jobs.JobExecutionPolicy.MaxAttemptsFor(JobKind.NotificationDelivery);
            // Pending sends it back through the queue for another attempt; anything else is this
            // delivery's final word — never erased by whatever a later, unrelated delivery does.
            delivery.Status = canRetry ? NotificationDeliveryStatus.Pending : NotificationDeliveryStatus.Failed;
            await db.SaveChangesAsync(ct);

            if (alert is not null) await RecordAttemptAsync(alert, NotificationResult.Failed(ex.Message), ct);

            throw;
        }
    }

    /// <summary>
    /// <inheritdoc cref="INotificationService.SendTelegramAsync"/> Delegates straight to
    /// <see cref="DispatchOnceAsync"/> — the exact private method an Alert's own Telegram channel
    /// runs through — rather than duplicating the HTTP call, the target JSON shape or the
    /// response/timeout handling. Severity is <see cref="AlertSeverity.Info"/>: Telegram's own sender
    /// (<see cref="SendTelegram"/>) never reads it, unlike Discord's colour-coded embed.
    /// </summary>
    public Task SendTelegramAsync(string encryptedTarget, string title, string body, CancellationToken ct) =>
        DispatchOnceAsync(AlertChannel.Telegram, encryptedTarget, AlertSeverity.Info, title, new ChannelBody(body, null), ct);

    /// <summary>
    /// Whether this delivery will route through the platform's own SMTP rather than a channel that
    /// carries its own server — an alert whose Email target names no host of its own, or any purpose
    /// with no alert at all (every transactional message and the admin fallback below).
    /// </summary>
    private bool UsesPlatformMailer(Alert? alert)
    {
        if (alert is null) return true;
        if (alert.Channel != AlertChannel.Email) return false;

        try
        {
            var target = string.IsNullOrEmpty(alert.EncryptedTarget) ? "{}" : protector.Unprotect(alert.EncryptedTarget);
            var t = JsonSerializer.Deserialize<EmailTarget>(target, TargetJson);
            return string.IsNullOrWhiteSpace(t?.Host) && !string.IsNullOrWhiteSpace(t?.To);
        }
        catch
        {
            // An unreadable target is a real problem, but not this method's to diagnose — the actual
            // attempt below will hit the same decryption/parse failure and record it properly.
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="delivery"/> and, unless it was already settled (the no-admin fallback
    /// below hands this a row that is already <see cref="NotificationDeliveryStatus.Suppressed"/>),
    /// queues the job that will attempt it.
    ///
    /// <para>
    /// Runs on a fresh scope of its own rather than the caller's shared <see cref="db"/> — the one
    /// thing N1's own spec calls out as easy to miss. <see cref="NotifyAsync"/> and
    /// <see cref="NotifyRuleAsync"/> are called from deep inside a raiser's own unit of work
    /// (<c>MetricsCollector</c> batches a whole tick into one save; <c>DeploymentPipeline</c>'s
    /// failure path already saves once and does not save again before this runs). The old
    /// <c>RecordAttemptAsync</c> called <c>SaveChangesAsync</c> on the shared context and so, as a
    /// side effect nobody asked for, flushed whatever the caller had not saved yet — a habit this must
    /// not inherit in the other direction: enqueuing here must neither depend on the caller saving
    /// afterwards (proven unsafe — <c>DeploymentPipeline</c> does not) nor force the caller's own
    /// half-built state to commit early. An isolated scope does both: it persists regardless of what
    /// the caller goes on to do, and it touches nothing the caller was still holding.
    /// </para>
    /// </summary>
    private async Task EnqueueDeliveryAsync(NotificationDelivery delivery, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        scopedDb.NotificationDeliveries.Add(delivery);
        await scopedDb.SaveChangesAsync(ct);

        if (delivery.Status == NotificationDeliveryStatus.Pending)
            await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .EnqueueAsync(JobKind.NotificationDelivery, delivery.Id, delivery.WorkspaceId, ct);
    }

    /// <summary>
    /// In-app is the sink that cannot fail (N3, 2026-08-16 notification-system spec, "told a person,
    /// not a channel") — the general case of the fix just below (<see cref="EnqueueFallbackToAdminsAsync"/>)
    /// made for its own one scenario. A row in this table cannot answer 404, cannot time out and needs
    /// no SMTP configured: it is the one copy of an event that always lands, so a workspace that has
    /// never configured a channel — or whose only channel is currently refusing everything — stops
    /// being a workspace nobody can reach, for every event this class raises, not only the one where
    /// no alert rule exists at all.
    ///
    /// <para>
    /// Every active member, not merely admins — N3 §7 Q2 answers differently for this row than N1
    /// answered it for the admin-only email fallback below: paging somebody's phone for every event
    /// nobody asked them about is noise a Viewer never signed up for, but a row waiting in an inbox
    /// they do not have to open costs them nothing until N5 makes it tunable.
    /// </para>
    ///
    /// <para>
    /// <b>N5 ("noise control"): "Not in N3: preferences" is no longer true.</b> Each member's own
    /// <c>NotificationPreference</c> — absent meaning <see cref="NotificationPreferenceDefaults"/>,
    /// which is <c>InApp = Immediate</c> for every event, byte-identical to N3's own unconditional
    /// behaviour — decides whether their in-app row is written at all, and whether their personal
    /// email is sent now, folded into their next digest, or skipped. A critical event
    /// (<see cref="NotificationEventClass.IsCritical"/>) is never affected by quiet hours on any
    /// channel and always gets at least one channel resolved <c>Immediate</c> — enforced twice: once
    /// when the preference was written (<c>NotificationPreferenceService.SetAsync</c>), and again here,
    /// belt-and-suspenders, in case a row was ever written by anything else. If neither channel comes
    /// out <c>Immediate</c> for a critical event — which a correctly-behaving preference service never
    /// allows — this forces the in-app row anyway rather than trust the data: a customer may choose
    /// where the last warning before suspension goes, not whether it exists, and that has to hold even
    /// against a bad row, not merely against a well-behaved caller.
    /// </para>
    ///
    /// <para>
    /// N4: rendered once per member, in that member's own <c>User.PreferredCulture</c> — this is the
    /// one place in the class where "the same event, two readers, two languages" is actually true,
    /// because it is the one place that knows every reader by name.
    /// </para>
    ///
    /// <para>
    /// Runs on its own isolated scope, the same reasoning <see cref="EnqueueDeliveryAsync"/> documents:
    /// this must persist regardless of what the caller does afterward, and must not flush whatever the
    /// caller's own unit of work (a batched collector tick, a pipeline's failure path) has not saved
    /// yet.
    /// </para>
    /// </summary>
    private async Task FanOutToMembersAsync(
        Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
    {
        // IgnoreQueryFilters(), with workspaceId spelled out explicitly right here as the only tenant
        // boundary — the trap this codebase's own do-not-change list warns about. Every raise site
        // before NotifyInAppOnlyAsync (Sub-project 4, 2026-08-20 platform-options plan) ran from a
        // background job's unscoped context, where WorkspaceMember's global filter is already inert
        // and this line would have been a no-op. NotifyInAppOnlyAsync's first caller
        // (AnnouncementNotifier) runs inside an HTTP request instead, fanning out to workspaces other
        // than the signed-in platform admin's own — without this, the filter silently narrowed every
        // one of those reads to "member of the admin's own workspace", which is either wrong or, for
        // every other workspace, empty. AnnouncementHttpTests is what caught it.
        var members = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == workspaceId && m.User!.IsActive)
            .Select(m => new
            {
                m.UserId, m.User!.PreferredCulture, m.User!.Email,
                m.User!.TimeZoneId, m.User!.QuietHoursStartHour, m.User!.QuietHoursEndHour
            })
            .Distinct()
            .ToListAsync(ct);

        if (members.Count == 0) return; // Nobody in the workspace at all — nothing to write.

        var isCritical = NotificationEventClass.IsCritical(evt.Type);
        var memberIds = members.Select(m => m.UserId).ToList();
        var preferences = await db.NotificationPreferences
            .Where(p => memberIds.Contains(p.UserId) && p.EventType == evt.Type)
            .ToListAsync(ct);
        var now = clock.UtcNow;

        NotificationPreferenceMode Resolve(Guid userId, NotificationChannel channel) =>
            preferences.FirstOrDefault(p => p.UserId == userId && p.Channel == channel)?.Mode
            ?? NotificationPreferenceDefaults.DefaultFor(evt.Type, channel);

        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        foreach (var member in members)
        {
            var rendered = catalog.Render(evt, member.PreferredCulture);
            var immediateLanded = false;

            var inAppMode = Resolve(member.UserId, NotificationChannel.InApp);
            if (inAppMode == NotificationPreferenceMode.Immediate)
            {
                scopedDb.UserNotifications.Add(new Domain.Notifications.UserNotification
                {
                    WorkspaceId = workspaceId, UserId = member.UserId, Severity = severity,
                    Title = rendered.Subject, Body = rendered.TextBody
                });
                immediateLanded = true;
            }

            var emailMode = Resolve(member.UserId, NotificationChannel.Email);
            // Quiet hours only ever touch the optional half of an event, and only ever the email
            // channel — see QuietHours' own doc for why in-app has nothing to gain from it.
            if (!isCritical && emailMode == NotificationPreferenceMode.Immediate
                && QuietHours.IsQuiet(member.QuietHoursStartHour, member.QuietHoursEndHour, member.TimeZoneId, now))
                emailMode = NotificationPreferenceMode.Digest;

            switch (emailMode)
            {
                case NotificationPreferenceMode.Immediate:
                    await EnqueueDeliveryAsync(new NotificationDelivery
                    {
                        WorkspaceId = workspaceId, Purpose = NotificationDeliveryPurpose.PersonalPreference,
                        Channel = AlertChannel.Email, RecipientAddress = member.Email, Severity = severity,
                        Subject = rendered.Subject,
                        EncryptedBody = protector.Protect(ChannelBody.Encode(rendered.TextBody, rendered.HtmlBody))
                    }, ct);
                    immediateLanded = true;
                    break;

                case NotificationPreferenceMode.Digest:
                    scopedDb.NotificationDigestEntries.Add(new Domain.Notifications.NotificationDigestEntry
                    {
                        UserId = member.UserId, WorkspaceId = workspaceId, EventType = evt.Type,
                        Severity = severity, Title = rendered.Subject, Body = rendered.TextBody
                    });
                    break;
            }

            // The belt-and-suspenders check described in this method's own doc comment.
            if (isCritical && !immediateLanded)
            {
                logger.LogWarning(
                    "{Event} is critical but resolved to no Immediate channel for user {UserId}; " +
                    "forcing the in-app row rather than trust a preference row that should never allow this.",
                    evt.Type, member.UserId);
                scopedDb.UserNotifications.Add(new Domain.Notifications.UserNotification
                {
                    WorkspaceId = workspaceId, UserId = member.UserId, Severity = severity,
                    Title = rendered.Subject, Body = rendered.TextBody
                });
            }
        }

        await scopedDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §3 way two, closed: a workspace with no alert rule at all now has somebody who was told — every
    /// workspace Admin, by email, rather than the fact vanishing the moment the dispatch loop ran zero
    /// times. Not every member: a Viewer or a Developer did not sign up to be paged for an
    /// unconfigured channel, and Admins are the smallest audience the owner's answer to §7 Q2 asks for.
    ///
    /// <para>
    /// N4: an admin is a real person with a real <c>User.PreferredCulture</c> — unlike a channel
    /// delivery, this renders per admin rather than once in the platform default.
    /// </para>
    /// </summary>
    private async Task EnqueueFallbackToAdminsAsync(
        Guid workspaceId, NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
    {
        var admins = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && m.Role == Domain.Common.WorkspaceRole.Admin
                        && m.User!.IsActive)
            .Select(m => new { m.User!.Email, m.User!.PreferredCulture })
            .Distinct()
            .ToListAsync(ct);

        if (admins.Count == 0)
        {
            // Recorded as such rather than silently doing nothing: even a workspace with nobody at
            // all to tell leaves a row a person can find later, instead of this looking identical to
            // a successful, quiet delivery.
            var (encryptedBody, subject) = RenderForChannel(evt);
            await EnqueueDeliveryAsync(new NotificationDelivery
            {
                WorkspaceId = workspaceId,
                Purpose = NotificationDeliveryPurpose.NoRecipientFallback,
                Channel = AlertChannel.Email,
                Severity = severity,
                Subject = subject,
                EncryptedBody = encryptedBody,
                Status = NotificationDeliveryStatus.Suppressed,
                LastError = "This workspace has no alert rule and no admin to notify instead.",
                LastAttemptAt = clock.UtcNow
            }, ct);
            return;
        }

        foreach (var admin in admins)
        {
            var rendered = catalog.Render(evt, admin.PreferredCulture);
            await EnqueueDeliveryAsync(new NotificationDelivery
            {
                WorkspaceId = workspaceId,
                Purpose = NotificationDeliveryPurpose.NoRecipientFallback,
                Channel = AlertChannel.Email,
                RecipientAddress = admin.Email,
                Severity = severity,
                Subject = rendered.Subject,
                EncryptedBody = protector.Protect(ChannelBody.Encode(rendered.TextBody, rendered.HtmlBody))
            }, ct);
        }
    }

    /// <summary>
    /// Turns an HTTP response into a verdict.
    ///
    /// This is the crux: the response used to be discarded, so a webhook answering 404 — a typo in the
    /// URL, a revoked Discord hook, a wrong Telegram chat id — was indistinguishable from one that
    /// worked, and the panel reported every one of them as sent.
    ///
    /// <para>
    /// §7 Q4(a): a 5xx is retryable, a 4xx is not — a bad gateway is the far end having a bad moment,
    /// a 404 is the message itself being refused, and retrying that three times over half an hour
    /// buries the one sentence that says why under three copies of itself.
    /// </para>
    /// </summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, string channel, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = "";
        try
        {
            var payload = (await response.Content.ReadAsStringAsync(ct)).Trim();
            // An API's error body names the mistake ("chat not found"); an HTML error page just fills
            // the message with markup, and the status code already said everything it has to say.
            if (payload.Length > 0 && !payload.StartsWith('<'))
                detail = " — " + (payload.Length > 200 ? payload[..200] : payload);
        }
        catch { /* the status alone is already the useful part */ }

        throw new NotificationChannelException(
            $"{channel} returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
            isRetryable: (int)response.StatusCode >= 500);
    }

    private async Task SendTelegram(string target, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<TelegramTarget>(target, TargetJson)!;
        var client = httpFactory.CreateClient();
        var text = $"*{title}*\n{body}";
        using var response = await client.PostAsJsonAsync(
            $"https://api.telegram.org/bot{Uri.EscapeDataString(t.BotToken)}/sendMessage",
            new { chat_id = t.ChatId, text, parse_mode = "Markdown" }, ct);
        await EnsureAcceptedAsync(response, "Telegram", ct);
    }

    private async Task SendDiscord(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target, TargetJson)!;
        GuardOutboundUrl(t.Url);
        var client = httpFactory.CreateClient();
        var color = severity switch { AlertSeverity.Critical => 15158332, AlertSeverity.Warning => 15844367, _ => 3066993 };
        using var response = await client.PostAsJsonAsync(
            t.Url, new { embeds = new[] { new { title, description = body, color } } }, ct);
        await EnsureAcceptedAsync(response, "Discord", ct);
    }

    private async Task SendWebhook(string target, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<UrlTarget>(target, TargetJson)!;
        GuardOutboundUrl(t.Url);
        var client = httpFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            t.Url, new { severity = severity.ToString(), title, body, at = DateTimeOffset.UtcNow }, ct);
        await EnsureAcceptedAsync(response, "The webhook", ct);
    }

    /// <summary>SSRF guard (doc 10 §2.8): refuse to call internal/reserved targets. Throws;
    /// DispatchSafe logs and swallows so a blocked channel never breaks a deploy/backup.</summary>
    private static void GuardOutboundUrl(string url)
    {
        if (!Security.UrlSafety.IsAllowedOutboundUrl(url, out var reason))
            throw new InvalidOperationException($"Refusing to call webhook URL: {reason}.");
    }

    /// <summary>
    /// The one channel that gets a real HTML alternative (N4) — everything else already speaks its own
    /// format. <see cref="ChannelBody.Html"/> is null for anything that reached this class before N4
    /// templated it (a password reset, an invite), and <see cref="PlatformMailer.SendAsync"/> sends
    /// plain-text-only mail in that case, exactly as it always has.
    /// </summary>
    private async Task SendEmail(string target, string title, ChannelBody body, CancellationToken ct)
    {
        var t = JsonSerializer.Deserialize<EmailTarget>(target, TargetJson)!;

        // An alert that names only a recipient uses the platform's own account — one SMTP password
        // typed once in platform settings, not once per alert. A full per-alert server still wins,
        // for the installation that routes alerts through somewhere else.
        if (string.IsNullOrWhiteSpace(t.Host) && !string.IsNullOrWhiteSpace(t.To))
        {
            await platformMailer.SendAsync(t.To, title, body.Text, body.Html, ct);
            return;
        }

        using var client = new SmtpClient(t.Host, t.Port)
        {
            EnableSsl = t.UseSsl,
            Credentials = new NetworkCredential(t.User, t.Password)
        };
        using var message = new MailMessage(t.From, t.To, title, body.Text);
        if (body.Html is { } html)
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
        await client.SendMailAsync(message, ct);
    }

    private sealed record TelegramTarget(string BotToken, string ChatId);
    private sealed record UrlTarget(string Url);
    private sealed record EmailTarget(string Host, int Port, string User, string Password, string From, string To, bool UseSsl);
}
