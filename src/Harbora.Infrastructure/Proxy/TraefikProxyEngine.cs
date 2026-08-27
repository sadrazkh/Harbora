using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// Renders <see cref="Route"/>s into a Traefik dynamic-config YAML document and applies it
/// atomically: write to a temp file, back up the current one, swap in place, and roll back the
/// file if anything throws. Traefik picks up the change via its file-provider watcher.
///
/// <para>
/// One file, one install: <see cref="TraefikOptions.DynamicConfigPath"/> is not per-tenant, so an
/// apply is always a statement about the whole platform's routing. That is why the routes come from
/// <see cref="IRouteCatalog"/> rather than from the caller, and why applies are serialised — two
/// writers sharing one file is two writers sharing one truth.
/// </para>
///
/// <para>
/// It is also why a route that fails validation is left out of the render rather than refusing it:
/// one file means one refusal stops everybody, and a route that cannot serve is not made to serve by
/// withholding everyone else's. What it does not mean is silence — see <c>WriteAsync</c>.
/// </para>
/// </summary>
public sealed class TraefikProxyEngine(
    IOptions<TraefikOptions> options,
    ISecretProtector protector,
    IRouteCatalog catalog,
    ILogger<TraefikProxyEngine> logger) : IProxyEngine
{
    /// <summary>
    /// Where a render waits while it is being written, next to the file it is going to become so the
    /// swap is a rename on one filesystem. A directory of its own, rather than a sibling
    /// <c>.tmp</c>, because the name now carries a per-attempt id and debris from a killed process
    /// should not accumulate in the directory Traefik reads.
    /// </summary>
    public const string StagingDirectoryName = ".harbora-apply";

    private readonly TraefikOptions _opt = options.Value;

    private bool CloudflareEnabled => File.Exists(_opt.CloudflareEnabledMarkerPath);
    private string ActiveCertResolver => CloudflareEnabled ? "cloudflare" : _opt.CertResolver;
    private int ActiveForwardedClientIpDepth =>
        CloudflareEnabled ? 1 : _opt.ForwardedClientIpDepth;

    /// <summary>
    /// One writer at a time. The engine is a singleton, and since jobs began running several at a
    /// time two applies overlapping is ordinary rather than rare: interleaved, one attempt's backup
    /// is taken after the other's swap, so a rollback restores a config that was never live.
    /// </summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>
    /// Test seam only — never set outside <c>Harbora.Tests</c>, a no-op in production. Awaited as the
    /// first step of <see cref="WriteAsync"/>, on the far side of <see cref="ApplyAllAsync"/>'s own
    /// read. A test built on <see cref="IRouteCatalog"/> alone can only watch the read: the read sits
    /// inside the apply gate whether or not the gate also covers the write, so a mutation that
    /// narrows the gate to the read alone is invisible to it. This hook sits exactly where that
    /// narrowing would show up.
    /// </summary>
    internal Func<Task>? TestOnlyBeforeWrite { get; set; }

    public ProxyConfigPreview Preview(IReadOnlyList<Route> routes)
        => new("yaml", Render(routes.Where(r => r.IsEnabled).ToList()));

    public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => ValidateWithOwnership(routes).Result;

    /// <summary>
    /// Same checks as <see cref="Validate"/>, but each error keeps the id of the route and the
    /// workspace that produced it. <see cref="Validate"/> throws that away, which is fine for the
    /// designer's own preview/validate endpoints — they only ever see one workspace's routes anyway —
    /// but <see cref="WriteAsync"/> validates the whole platform and needs to know, per error, whose
    /// route it was before it can decide what a caller is allowed to be told about it.
    ///
    /// <para>
    /// Every route is checked, switched on or not. A disabled route serves nothing today, which is
    /// why this used to skip them — but the designer's save gate asks this same question, and a row
    /// nobody checked is a row that gets switched on later by the deployment that owns its host,
    /// which sets the upstream and <c>IsEnabled</c> and never looks at the fields it did not write.
    /// That is how a redirect with no target, saved with the Enabled box cleared, became an enabled
    /// route no apply would accept. The duplicate host+path <b>warning</b> stays on the enabled ones
    /// alone: it is a statement about which of two live routes wins, and a route that is off wins
    /// nothing.
    /// </para>
    /// </summary>
    private static (ProxyValidationResult Result, List<(Guid WorkspaceId, Guid RouteId, string Message)> Tagged)
        ValidateWithOwnership(IReadOnlyList<Route> routes)
    {
        var tagged = new List<(Guid WorkspaceId, Guid RouteId, string Message)>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in routes)
        {
            if (string.IsNullOrWhiteSpace(r.Host))
                tagged.Add((r.WorkspaceId, r.Id, $"Route {r.Id}: host is required."));
            if (r.Type != RouteType.Redirect && string.IsNullOrWhiteSpace(r.TargetService))
                tagged.Add((r.WorkspaceId, r.Id, $"Route {r.Host}: an upstream service is required."));
            if (r.TargetPort is <= 0 or > 65535)
                tagged.Add((r.WorkspaceId, r.Id, $"Route {r.Host}: target port {r.TargetPort} is out of range."));
            if (r.Type == RouteType.Redirect && string.IsNullOrWhiteSpace(r.RedirectTo))
                tagged.Add((r.WorkspaceId, r.Id, $"Route {r.Host}: redirect target is required."));

            var key = $"{r.Host}{r.PathPrefix}";
            if (r.IsEnabled && !seen.Add(key))
                warnings.Add($"Duplicate host+path '{key}'; the higher-priority route wins.");

            if (r.CustomHeadersJson is { Length: > 0 } &&
                !TryParseHeaders(r.CustomHeadersJson, out _))
                tagged.Add((r.WorkspaceId, r.Id, $"Route {r.Host}: custom headers are not valid JSON."));
        }

        var errors = tagged.Select(t => t.Message).ToList();
        return (new ProxyValidationResult(errors.Count == 0, errors, warnings), tagged);
    }

    /// <summary>
    /// What a validation failure is allowed to say back to whoever triggered the apply. The caller's
    /// own invalid routes are named in full — they can act on those — everything else is reduced to a
    /// count, because naming another workspace's hostname, and saying it is misconfigured, is not this
    /// caller's to know. See <see cref="IProxyEngine.ApplyAllAsync"/> for why.
    ///
    /// <para>
    /// Only ever called when the caller owns at least one of the failing routes: an apply is no
    /// longer refused for anybody else's row, so there is no longer a caller who has to be told
    /// about a failure with nothing in it for them. The count of the others stays, because "your
    /// route is broken, and it is not the only one" is a different thing to walk into.
    /// </para>
    /// </summary>
    private static string CallerSafeValidationError(
        IReadOnlyList<(Guid WorkspaceId, Guid RouteId, string Message)> errors, Guid callerWorkspaceId)
    {
        var own = errors.Where(e => e.WorkspaceId == callerWorkspaceId)
            .Select(e => e.Message).Distinct().ToList();
        var elsewhereCount = errors
            .Where(e => e.WorkspaceId != callerWorkspaceId)
            .Select(e => e.RouteId)
            .Distinct()
            .Count();

        var ownText = string.Join("; ", own);
        return elsewhereCount == 0
            ? ownText
            : $"{ownText} ({elsewhereCount} other route(s) elsewhere on the platform also failed " +
              "validation and were left out of the configuration; see the server log.)";
    }

    public async Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct)
    {
        // Held across the read as well as the write. Rendering from routes read before waiting for
        // the gate would let a slow attempt publish a picture of the platform that a faster one has
        // already superseded — the file would be valid and out of date, which is the harder failure
        // to see.
        await _applyGate.WaitAsync(ct);
        try
        {
            var enabled = (await catalog.AllEnabledAsync(ct)).Where(r => r.IsEnabled).ToList();
            return await WriteAsync(enabled, callerWorkspaceId, ct);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Validate → drop what cannot serve → write temp → back up what is live → swap. Every failure
    /// after the render puts the backup back, and says whether it managed to.
    /// </summary>
    private async Task<ProxyApplyResult> WriteAsync(
        IReadOnlyList<Route> enabled, Guid? callerWorkspaceId, CancellationToken ct)
    {
        if (TestOnlyBeforeWrite is not null) await TestOnlyBeforeWrite();

        // Validation is platform-wide, because the file is. What is done about a failure is not.
        //
        // Refusing the whole apply was right while the alternative was silently dropping a row from
        // a file the caller believed described their own workspace. It stopped being right once the
        // file described everybody: one route anywhere — and a disabled row nobody validated, turned
        // on by its own deployment, is how one got there — refused every apply on the install, and
        // since a failed apply now fails the deployment, that is every tenant's deploys, route
        // saves, protection changes and Adminer sessions, until an operator found the row.
        //
        // So the route is left out instead. A route that fails validation was never going to serve
        // whatever we do with it, and dropping it keeps the rest of the platform routed. What is
        // NOT dropped is the knowledge: every one of them is named in the log at Error, and named
        // again in the file itself, and the workspace that owns one is told its apply did not do
        // everything it was asked (below).
        var (_, invalid) = ValidateWithOwnership(enabled);
        var excluded = invalid.Select(e => e.RouteId).ToHashSet();
        var renderable = excluded.Count == 0 ? enabled : enabled.Where(r => !excluded.Contains(r.Id)).ToList();
        var notes = ExclusionNotes(invalid);

        if (excluded.Count > 0)
            logger.LogError(
                "Left {Count} route(s) out of the platform proxy config because they failed " +
                "validation and would not have served: {Errors}",
                excluded.Count, string.Join("; ", notes));

        // Whose failure this is. A caller can act on their own row, so they are owed the refusal —
        // a deployment that switched its own route on and had it dropped has not deployed that
        // domain, and reporting success would be the lie this phase exists to remove. A caller who
        // owns none of them can act on nothing, and failing their deployment for a stranger's row
        // is the outage above.
        var ownFailed = callerWorkspaceId is { } caller && invalid.Any(e => e.WorkspaceId == caller);

        var target = _opt.DynamicConfigPath;
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);

        var backup = target + ".bak";
        var staging = Path.Combine(dir, StagingDirectoryName);
        // Unique per attempt: a fixed name is a file two attempts — or two panel processes — both
        // believe they own, so one moves the other's half-written render into place. The .tmp
        // suffix stays because Traefik's file provider ignores extensions it does not know.
        var tmp = Path.Combine(staging, $"{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(tmp, Render(renderable, notes), ct);
            if (File.Exists(target)) File.Copy(target, backup, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            logger.LogInformation(
                "Applied Traefik dynamic config with {Count} route(s) across the platform.", renderable.Count);
            return ownFailed
                ? new ProxyApplyResult(
                    false, CallerSafeValidationError(invalid, callerWorkspaceId!.Value), false)
                : new ProxyApplyResult(true, null, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply proxy config; rolling back.");
            var rolledBack = false;
            if (File.Exists(backup))
            {
                File.Copy(backup, target, overwrite: true);
                rolledBack = true;
            }
            if (File.Exists(tmp)) File.Delete(tmp);
            return new ProxyApplyResult(false, ex.Message, rolledBack);
        }
    }

    // --- rendering ---

    /// <summary>
    /// One line per route this render refuses to carry, naming the route, its workspace and the
    /// reason. Derived from the validation result, so the same route set always produces the same
    /// lines — the file is watched and compared, and notes that shuffled would be a change where
    /// there was none.
    /// </summary>
    private static IReadOnlyList<string> ExclusionNotes(
        IReadOnlyList<(Guid WorkspaceId, Guid RouteId, string Message)> errors) =>
        errors.Select(e => $"route {e.RouteId} (workspace {e.WorkspaceId}): {e.Message}")
            .Distinct()
            .ToList();

    private string Render(IReadOnlyList<Route> routes, IReadOnlyList<string>? excluded = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Harbora — do not edit by hand.");
        // Said here as well as in the log because this file is the first thing anyone opens when a
        // domain stops answering, and "it is simply not in here" is the question a comment answers
        // and an absence does not. Traefik ignores comments; an operator cannot.
        if (excluded is { Count: > 0 })
        {
            sb.AppendLine($"# {excluded.Count} route(s) left out of this render — each one failed " +
                          "validation and would not have served:");
            foreach (var note in excluded) sb.AppendLine($"#   {note}");
        }
        sb.AppendLine("http:");
        sb.AppendLine("  routers:");
        foreach (var r in routes.OrderByDescending(r => r.Priority))
            RenderRouter(sb, r);

        sb.AppendLine("  services:");
        foreach (var r in routes.Where(r => r.Type != RouteType.Redirect))
            RenderService(sb, r);

        sb.AppendLine("  middlewares:");
        foreach (var r in routes)
            RenderMiddlewares(sb, r);

        return sb.ToString();
    }

    private void RenderRouter(StringBuilder sb, Route r)
    {
        var name = RouterName(r);
        var rule = $"Host(`{r.Host}`)";
        if (!string.IsNullOrWhiteSpace(r.PathPrefix) && r.PathPrefix != "/")
            rule += $" && PathPrefix(`{r.PathPrefix}`)";

        var mws = MiddlewareNames(r);

        sb.AppendLine($"    {name}:");
        sb.AppendLine($"      rule: \"{rule}\"");
        sb.AppendLine($"      entryPoints: [\"{_opt.EntryPointWebSecure}\"]");
        sb.AppendLine($"      priority: {Math.Max(1, r.Priority)}");
        sb.AppendLine($"      service: {(r.Type == RouteType.Redirect ? "noop@internal" : name + "-svc")}");
        if (mws.Count > 0)
            sb.AppendLine($"      middlewares: [{string.Join(", ", mws)}]");
        if (r.SslEnabled)
        {
            sb.AppendLine("      tls:");
            sb.AppendLine($"        certResolver: {ActiveCertResolver}");
        }
    }

    private void RenderService(StringBuilder sb, Route r)
    {
        sb.AppendLine($"    {RouterName(r)}-svc:");
        sb.AppendLine("      loadBalancer:");
        sb.AppendLine("        servers:");
        // Ordinarily just the one — RouteUpstreams.All returns TargetService/TargetPort alone when
        // ExtraUpstreamsJson is empty, which is every route the designer creates by hand. A deployment
        // that started more than one replica container populated the extras, and every one of them
        // gets a server line here: this loadBalancer, not a second router per replica, is what spreads
        // traffic across them.
        foreach (var upstream in Domain.Networking.RouteUpstreams.All(r))
            sb.AppendLine($"          - url: \"http://{upstream.Host}:{upstream.Port}\"");

        // Traefik polls this path on every server above and stops sending it traffic the moment a
        // poll fails — no panel-side loop, no re-render on a container dying between deploys. Only
        // rendered when the deploying app actually recorded a path: an app with none configured a
        // health check with no fixed answer, and every server here would then read as failing forever.
        if (!string.IsNullOrWhiteSpace(r.LoadBalancerHealthCheckPath))
        {
            sb.AppendLine("        healthCheck:");
            sb.AppendLine($"          path: \"{r.LoadBalancerHealthCheckPath}\"");
            sb.AppendLine("          interval: \"10s\"");
            sb.AppendLine("          timeout: \"5s\"");
        }
    }

    private void RenderMiddlewares(StringBuilder sb, Route r)
    {
        var name = RouterName(r);
        if (r.RedirectHttpToHttps)
        {
            sb.AppendLine($"    {name}-https:");
            sb.AppendLine("      redirectScheme:");
            sb.AppendLine("        scheme: https");
            sb.AppendLine("        permanent: true");
        }
        if (r.Type == RouteType.Redirect && !string.IsNullOrWhiteSpace(r.RedirectTo))
        {
            sb.AppendLine($"    {name}-redirect:");
            sb.AppendLine("      redirectRegex:");
            sb.AppendLine("        regex: \"^https?://[^/]+/(.*)\"");
            sb.AppendLine($"        replacement: \"{r.RedirectTo}\"");
            sb.AppendLine("        permanent: false");
        }
        if (r.CustomHeadersJson is { Length: > 0 } && TryParseHeaders(r.CustomHeadersJson, out var headers))
        {
            sb.AppendLine($"    {name}-headers:");
            sb.AppendLine("      headers:");
            sb.AppendLine("        customResponseHeaders:");
            foreach (var (k, v) in headers)
                sb.AppendLine($"          {k}: \"{v}\"");
        }
        if (r.BasicAuthEnabled && !string.IsNullOrWhiteSpace(r.BasicAuthUsersEncrypted))
        {
            var users = SafeDecrypt(r.BasicAuthUsersEncrypted!)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            sb.AppendLine($"    {name}-auth:");
            sb.AppendLine("      basicAuth:");
            sb.AppendLine("        users:");
            foreach (var u in users)
                sb.AppendLine($"          - \"{u}\"");
        }
        // Only the entries that parse. A malformed one written through would be rejected by Traefik
        // on apply — after the operator left the page believing the route was protected.
        var allowed = AccessList.Parse(r.IpAllowlist, out _);
        if (allowed.Count > 0)
        {
            sb.AppendLine($"    {name}-ips:");
            sb.AppendLine("      ipAllowList:");
            if (ActiveForwardedClientIpDepth > 0)
            {
                sb.AppendLine("        ipStrategy:");
                sb.AppendLine($"          depth: {ActiveForwardedClientIpDepth}");
            }
            sb.AppendLine("        sourceRange:");
            foreach (var entry in allowed)
                sb.AppendLine($"          - \"{entry}\"");
        }
        // C3 (2026-08-27 what's-left plan): per-app rate limiting. average/burst are the customer's
        // own numbers (requests per minute, and how many of those may arrive at once); the period
        // itself is fixed rather than surfaced — see AppRateLimitPolicy's own remarks for why. Clamped
        // to at least 1 rather than skipped on a stray zero/negative row: a route that reads
        // "protected" in the database must never render as unlimited.
        if (r.RateLimitEnabled)
        {
            sb.AppendLine($"    {name}-ratelimit:");
            sb.AppendLine("      rateLimit:");
            sb.AppendLine($"        average: {Math.Max(1, r.RateLimitAverage)}");
            sb.AppendLine($"        burst: {Math.Max(1, r.RateLimitBurst)}");
            sb.AppendLine($"        period: \"{Domain.Apps.AppRateLimitPolicy.PeriodSeconds}s\"");
        }
    }

    private List<string> MiddlewareNames(Route r)
    {
        var name = RouterName(r);
        var list = new List<string>();
        if (r.RedirectHttpToHttps) list.Add($"{name}-https");
        if (r.Type == RouteType.Redirect && !string.IsNullOrWhiteSpace(r.RedirectTo)) list.Add($"{name}-redirect");
        if (r.CustomHeadersJson is { Length: > 0 }) list.Add($"{name}-headers");
        // Only reference the auth middleware when credentials actually exist, so the router never
        // points at a middleware we didn't render.
        if (r.BasicAuthEnabled && !string.IsNullOrWhiteSpace(r.BasicAuthUsersEncrypted)) list.Add($"{name}-auth");
        // Same rule as the renderer, so the router never names a middleware that was not written.
        if (AccessList.Parse(r.IpAllowlist, out _).Count > 0) list.Add($"{name}-ips");
        if (r.RateLimitEnabled) list.Add($"{name}-ratelimit");
        return list;
    }

    private static string RouterName(Route r) =>
        "r-" + r.Id.ToString("N")[..12];

    private string SafeDecrypt(string cipher)
    {
        try { return protector.Unprotect(cipher); }
        catch { return string.Empty; }
    }

    private static bool TryParseHeaders(string json, out Dictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return false;
            headers = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
