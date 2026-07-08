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
/// </summary>
public sealed class TraefikProxyEngine(
    IOptions<TraefikOptions> options,
    ISecretProtector protector,
    ILogger<TraefikProxyEngine> logger) : IProxyEngine
{
    private readonly TraefikOptions _opt = options.Value;

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

    public async Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<Route> routes, CancellationToken ct)
    {
        var enabled = routes.Where(r => r.IsEnabled).ToList();
        var validation = Validate(enabled);
        if (!validation.IsValid)
            return new ProxyApplyResult(false, string.Join("; ", validation.Errors), false);

        var target = _opt.DynamicConfigPath;
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);

        var backup = target + ".bak";
        var tmp = target + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tmp, Render(enabled), ct);
            if (File.Exists(target)) File.Copy(target, backup, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            logger.LogInformation("Applied Traefik dynamic config with {Count} route(s).", enabled.Count);
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
