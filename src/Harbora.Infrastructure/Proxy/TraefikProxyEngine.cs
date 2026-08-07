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

    /// <summary>
    /// One writer at a time. The engine is a singleton, and since jobs began running several at a
    /// time two applies overlapping is ordinary rather than rare: interleaved, one attempt's backup
    /// is taken after the other's swap, so a rollback restores a config that was never live.
    /// </summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public ProxyConfigPreview Preview(IReadOnlyList<Route> routes)
        => new("yaml", Render(routes.Where(r => r.IsEnabled).ToList()));

    public ProxyValidationResult Validate(IReadOnlyList<Route> routes)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in routes.Where(r => r.IsEnabled))
        {
            if (string.IsNullOrWhiteSpace(r.Host))
                errors.Add($"Route {r.Id}: host is required.");
            if (r.Type != RouteType.Redirect && string.IsNullOrWhiteSpace(r.TargetService))
                errors.Add($"Route {r.Host}: an upstream service is required.");
            if (r.TargetPort is <= 0 or > 65535)
                errors.Add($"Route {r.Host}: target port {r.TargetPort} is out of range.");
            if (r.Type == RouteType.Redirect && string.IsNullOrWhiteSpace(r.RedirectTo))
                errors.Add($"Route {r.Host}: redirect target is required.");

            var key = $"{r.Host}{r.PathPrefix}";
            if (!seen.Add(key))
                warnings.Add($"Duplicate host+path '{key}'; the higher-priority route wins.");

            if (r.CustomHeadersJson is { Length: > 0 } &&
                !TryParseHeaders(r.CustomHeadersJson, out _))
                errors.Add($"Route {r.Host}: custom headers are not valid JSON.");
        }

        return new ProxyValidationResult(errors.Count == 0, errors, warnings);
    }

    public async Task<ProxyApplyResult> ApplyAllAsync(CancellationToken ct)
    {
        // Held across the read as well as the write. Rendering from routes read before waiting for
        // the gate would let a slow attempt publish a picture of the platform that a faster one has
        // already superseded — the file would be valid and out of date, which is the harder failure
        // to see.
        await _applyGate.WaitAsync(ct);
        try
        {
            var enabled = (await catalog.AllEnabledAsync(ct)).Where(r => r.IsEnabled).ToList();
            return await WriteAsync(enabled, ct);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Validate → write temp → back up what is live → swap. Every failure after the validation gate
    /// puts the backup back, and says whether it managed to.
    /// </summary>
    private async Task<ProxyApplyResult> WriteAsync(IReadOnlyList<Route> enabled, CancellationToken ct)
    {
        var validation = Validate(enabled);
        if (!validation.IsValid)
            return new ProxyApplyResult(false, string.Join("; ", validation.Errors), false);

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
            await File.WriteAllTextAsync(tmp, Render(enabled), ct);
            if (File.Exists(target)) File.Copy(target, backup, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            logger.LogInformation(
                "Applied Traefik dynamic config with {Count} route(s) across the platform.", enabled.Count);
            return new ProxyApplyResult(true, null, false);
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

    private string Render(IReadOnlyList<Route> routes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Harbora — do not edit by hand.");
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
            sb.AppendLine($"        certResolver: {_opt.CertResolver}");
        }
    }

    private void RenderService(StringBuilder sb, Route r)
    {
        sb.AppendLine($"    {RouterName(r)}-svc:");
        sb.AppendLine("      loadBalancer:");
        sb.AppendLine("        servers:");
        sb.AppendLine($"          - url: \"http://{r.TargetService}:{r.TargetPort}\"");
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
            sb.AppendLine("        sourceRange:");
            foreach (var entry in allowed)
                sb.AppendLine($"          - \"{entry}\"");
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
