using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Networking;

public sealed record CloudflarePanelState(
    bool Enabled,
    bool HasToken,
    string? Zone,
    DateTimeOffset? LastVerifiedAt,
    string PanelDomain,
    string RootDomain,
    string? S3Domain);

public sealed record CloudflareApplyResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns Cloudflare from a host-only deployment switch into an operator setting. The long-lived
/// copy of the token is encrypted in PostgreSQL; the plaintext copy exists only in the mode-600
/// file Traefik's Cloudflare provider reads through its <c>_FILE</c> environment variable.
/// </summary>
public sealed class CloudflarePlatformService(
    HarboraDbContext db,
    ISecretProtector protector,
    CloudflareApiClient cloudflare,
    IProxyEngine proxy,
    IConfiguration config,
    ISystemClock clock,
    ILogger<CloudflarePlatformService> logger)
{
    private string TokenFile => config["Cloudflare:TokenFilePath"] ?? "/dynamic/secrets/cloudflare-token";
    private string DynamicConfig => config["Cloudflare:DynamicConfigPath"] ?? "/dynamic/cloudflare.yml";
    private string Marker => config["Cloudflare:EnabledMarkerPath"] ?? "/dynamic/cloudflare.enabled";
    private string PanelDomain => (config["PANEL_DOMAIN"] ?? "").Trim().TrimEnd('.');
    private string RootDomain => (config["ROOT_DOMAIN"] ?? "").Trim().TrimEnd('.');
    private string? S3Domain => string.IsNullOrWhiteSpace(config["S3_DOMAIN"])
        ? null
        : config["S3_DOMAIN"]!.Trim().TrimEnd('.');

    public async Task<CloudflarePanelState> GetStateAsync(CancellationToken ct)
    {
        var values = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.CloudflareEnabled ||
                        s.Key == SettingKeys.CloudflareToken ||
                        s.Key == SettingKeys.CloudflareZone ||
                        s.Key == SettingKeys.CloudflareLastVerifiedAt)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        values.TryGetValue(SettingKeys.CloudflareLastVerifiedAt, out var verified);
        // Deployments that enabled the recovery compose overlay before this panel existed remain
        // accurately reported as active until the token is imported into Harbora.
        var enabledByDeployment = string.Equals(
            config["Traefik:CertResolver"], "cloudflare", StringComparison.OrdinalIgnoreCase);
        return new CloudflarePanelState(
            enabledByDeployment ||
                (values.TryGetValue(SettingKeys.CloudflareEnabled, out var enabled) &&
                 string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) && File.Exists(Marker)),
            values.TryGetValue(SettingKeys.CloudflareToken, out var token) && !string.IsNullOrWhiteSpace(token),
            values.GetValueOrDefault(SettingKeys.CloudflareZone),
            DateTimeOffset.TryParse(verified, out var at) ? at : null,
            PanelDomain,
            RootDomain,
            S3Domain);
    }

    public async Task<CloudflareApplyResult> TestAsync(string? suppliedToken, string zone, CancellationToken ct)
    {
        try
        {
            var token = await ResolveTokenAsync(suppliedToken, ct);
            var zoneId = await VerifyAndFindZoneAsync(token, NormalizeZone(zone), ct);
            return new(true, $"Cloudflare accepted the token and exposed zone {zone} ({zoneId[..Math.Min(8, zoneId.Length)]}…).", []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cloudflare token verification failed for zone {Zone}.", zone);
            return new(false, ex.Message, []);
        }
    }

    public async Task<CloudflareApplyResult> EnableAsync(
        string? suppliedToken,
        string zone,
        bool proxyRecords,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        try
        {
            var normalizedZone = NormalizeZone(zone);
            var token = await ResolveTokenAsync(suppliedToken, ct);
            var zoneId = await VerifyAndFindZoneAsync(token, normalizedZone, ct);

            // A proxied HTTPS origin must never be left in Flexible mode. Treat an inability to set
            // strict as a failed activation rather than saving a state that is known to loop.
            await cloudflare.SendAsync(token, HttpMethod.Patch, $"zones/{zoneId}/settings/ssl",
                "{\"value\":\"strict\"}", ct);

            WriteSecretFile(TokenFile, token);
            WriteAtomic(Marker, $"enabled {clock.UtcNow:O}\n");
            WriteAtomic(DynamicConfig, RenderDynamicConfig());

            // Prepare every origin route before changing public DNS. Once a record turns orange,
            // Cloudflare may send traffic immediately and must find DNS-01/strict-ready routes.
            var applied = await proxy.ApplyAllAsync(callerWorkspaceId: null, ct);
            if (!applied.Success)
                warnings.Add("Cloudflare was enabled, but one or more managed app routes could not be re-rendered: " + applied.Error);

            if (proxyRecords)
            {
                foreach (var host in ManagedDnsNames(normalizedZone))
                {
                    var changed = await SetExistingRecordProxiedAsync(token, zoneId, host, ct);
                    if (!changed)
                        warnings.Add($"No existing A/AAAA/CNAME record was found for {host}; create it in Cloudflare, then enable Proxied.");
                }
            }

            await WriteSettingAsync(SettingKeys.CloudflareToken, protector.Protect(token), secret: true, ct);
            await WriteSettingAsync(SettingKeys.CloudflareZone, normalizedZone, secret: false, ct);
            await WriteSettingAsync(SettingKeys.CloudflareEnabled, "true", secret: false, ct);
            await WriteSettingAsync(SettingKeys.CloudflareLastVerifiedAt, clock.UtcNow.ToString("O"), secret: false, ct);
            await db.SaveChangesAsync(ct);

            return new(true,
                "Cloudflare mode is active. Traefik now uses DNS-01, and Cloudflare SSL mode is Full (strict).",
                warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not activate Cloudflare from the platform panel.");
            return new(false, ex.Message, warnings);
        }
    }

    private IEnumerable<string> ManagedDnsNames(string zone)
    {
        if (BelongsToZone(PanelDomain, zone)) yield return PanelDomain;
        if (S3Domain is { Length: > 0 } s3 && BelongsToZone(s3, zone)) yield return s3;
        if (BelongsToZone(RootDomain, zone)) yield return "*." + RootDomain;
    }

    private static bool BelongsToZone(string host, string zone) =>
        host.Equals(zone, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> SetExistingRecordProxiedAsync(
        string token, string zoneId, string host, CancellationToken ct)
    {
        using var doc = await cloudflare.SendAsync(token, HttpMethod.Get,
            $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(host)}&per_page=100", null, ct);

        var changed = false;
        foreach (var record in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var type = record.GetProperty("type").GetString();
            if (type is not ("A" or "AAAA" or "CNAME")) continue;
            if (record.TryGetProperty("proxied", out var proxied) && proxied.GetBoolean())
            {
                changed = true;
                continue;
            }

            await cloudflare.SendAsync(token, HttpMethod.Patch,
                $"zones/{zoneId}/dns_records/{record.GetProperty("id").GetString()}",
                "{\"proxied\":true}", ct);
            changed = true;
        }
        return changed;
    }

    /// <summary>Verify-then-locate, in the shape this class has always used: an invalid token fails
    /// here before any zone lookup, and a valid-but-too-narrow token fails with the same "add
    /// Zone:Read" wording <see cref="CloudflareApiClient.FindZoneIdAsync"/> already throws.</summary>
    private async Task<string> VerifyAndFindZoneAsync(string token, string zone, CancellationToken ct)
    {
        await cloudflare.VerifyTokenAsync(token, ct);
        return await cloudflare.FindZoneIdAsync(token, zone, ct);
    }

    private async Task<string> ResolveTokenAsync(string? supplied, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied.Trim();
        var stored = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.CloudflareToken).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(stored))
            throw new InvalidOperationException("Enter a Cloudflare API token first.");
        return protector.Unprotect(stored);
    }

    private static string NormalizeZone(string zone)
    {
        var value = (zone ?? "").Trim().Trim('.').ToLowerInvariant();
        if (Uri.CheckHostName(value) != UriHostNameType.Dns || !value.Contains('.'))
            throw new InvalidOperationException("Enter the Cloudflare zone name, for example example.com.");
        return value;
    }

    private async Task WriteSettingAsync(string key, string value, bool secret, CancellationToken ct)
    {
        var row = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new Setting { Key = key };
            db.Settings.Add(row);
        }
        row.Value = value;
        row.IsSecret = secret;
    }

    private string RenderDynamicConfig()
    {
        if (Uri.CheckHostName(PanelDomain) != UriHostNameType.Dns)
            throw new InvalidOperationException("PANEL_DOMAIN is not a valid DNS hostname.");

        var yaml = new StringBuilder()
            .AppendLine("# Generated by Harbora's Cloudflare settings. Do not put secrets here.")
            .AppendLine("http:")
            .AppendLine("  routers:")
            .AppendLine("    harbora-panel-cloudflare:")
            .AppendLine($"      rule: \"Host(`{PanelDomain}`)\"")
            .AppendLine("      entryPoints: [websecure]")
            .AppendLine("      priority: 10000")
            .AppendLine("      service: harbora-panel-cloudflare")
            .AppendLine("      tls:")
            .AppendLine("        certResolver: cloudflare");

        if (S3Domain is { Length: > 0 } s3 && Uri.CheckHostName(s3) == UriHostNameType.Dns)
        {
            yaml.AppendLine("    harbora-s3-cloudflare:")
                .AppendLine($"      rule: \"Host(`{s3}`)\"")
                .AppendLine("      entryPoints: [websecure]")
                .AppendLine("      priority: 10000")
                .AppendLine("      service: harbora-s3-cloudflare")
                .AppendLine("      tls:")
                .AppendLine("        certResolver: cloudflare");
        }

        yaml.AppendLine("  services:")
            .AppendLine("    harbora-panel-cloudflare:")
            .AppendLine("      loadBalancer:")
            .AppendLine("        servers:")
            .AppendLine("          - url: \"http://harbora-panel:8080\"");
        if (S3Domain is { Length: > 0 } validS3 && Uri.CheckHostName(validS3) == UriHostNameType.Dns)
            yaml.AppendLine("    harbora-s3-cloudflare:")
                .AppendLine("      loadBalancer:")
                .AppendLine("        servers:")
                .AppendLine("          - url: \"http://harbora-minio:9000\"");
        return yaml.ToString();
    }

    private static void WriteSecretFile(string path, string content)
    {
        WriteAtomic(path, content + "\n");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"No directory for {path}.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
