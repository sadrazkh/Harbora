using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Networking;

/// <summary>Whether a workspace has its own Cloudflare token, and what the last live use of it said.
/// <see cref="LastVerificationError"/> is read back onto the page instead of an empty records table —
/// an empty table reads as "you have no records", and a token that cannot reach Cloudflare is a
/// different fact than that.</summary>
public sealed record CustomerDnsState(bool HasToken, DateTimeOffset? LastVerifiedAt, string? LastVerificationError);

/// <summary>The outcome of a mutation (save token, remove token, create/delete a record) — a
/// message honest enough to show verbatim, never "done" for a call that failed.</summary>
public sealed record CustomerDnsOutcome(bool Success, string Message);

/// <summary>Every zone the workspace's token can see, or the exact reason it could not be asked.</summary>
public sealed record CustomerDnsZonesResult(bool Success, IReadOnlyList<CloudflareZone> Zones, string? Error)
{
    public static CustomerDnsZonesResult Fail(string error) => new(false, [], error);
}

/// <summary>Every record of a supported type in one zone, or the exact reason it could not be asked.</summary>
public sealed record CustomerDnsRecordsResult(bool Success, IReadOnlyList<CloudflareDnsRecord> Records, string? Error)
{
    public static CustomerDnsRecordsResult Fail(string error) => new(false, [], error);
}

/// <summary>
/// A workspace's own bring-your-own Cloudflare token (F9, 2026-08-21 functions-and-services plan,
/// decision 5): save/verify it, list the zones and records it can see, and add/delete A, AAAA,
/// CNAME, TXT and MX records through it. v1 scope only — no zone creation, no DNSSEC, no bulk
/// import.
///
/// <para>
/// Built on <see cref="CloudflareApiClient"/>, the same HTTP transport
/// <see cref="CloudflarePlatformService"/> uses for the platform's own token — one calling
/// convention, two entirely separate credential stores. This class never reads a
/// <c>Setting</c> row (the platform's own token) and <see cref="CloudflarePlatformService"/> never
/// reads a <see cref="CustomerDnsCredential"/> row; each resolves only the token its own caller
/// handed it.
/// </para>
///
/// <para>
/// Every method takes the caller's own <c>workspaceId</c> and filters by it explicitly, on top of
/// <see cref="CustomerDnsCredential"/>'s own ambient query filter — the same belt-and-braces the
/// entity's own mapping comment asks for, so a workspace can never resolve another's token even if
/// one of the two guards is ever weakened on its own.
/// </para>
/// </summary>
public sealed class CustomerCloudflareService(
    HarboraDbContext db,
    CloudflareApiClient cloudflare,
    ISecretProtector protector,
    ISystemClock clock,
    ILogger<CustomerCloudflareService> logger)
{
    /// <summary>The only record types F9 manages. Anything else Cloudflare might return (NS, SOA,
    /// CAA, SRV, …) is left alone — not hidden, just out of v1 scope.</summary>
    public static readonly IReadOnlyCollection<string> SupportedTypes = new HashSet<string>(
        ["A", "AAAA", "CNAME", "TXT", "MX"], StringComparer.OrdinalIgnoreCase);

    public async Task<CustomerDnsState> GetStateAsync(Guid workspaceId, CancellationToken ct)
    {
        var row = await db.CustomerDnsCredentials
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
        return row is null
            ? new CustomerDnsState(false, null, null)
            : new CustomerDnsState(true, row.LastVerifiedAt, row.LastVerificationError);
    }

    /// <summary>
    /// Verifies the token live before storing anything — a token Cloudflare itself rejects is
    /// refused outright, the same way <see cref="CloudflarePlatformService.EnableAsync"/> leaves
    /// nothing behind for a token that cannot read its zone.
    /// </summary>
    public async Task<CustomerDnsOutcome> SaveTokenAsync(Guid workspaceId, string? token, CancellationToken ct)
    {
        var trimmed = (token ?? "").Trim();
        if (trimmed.Length == 0)
            return new CustomerDnsOutcome(false, "Enter a Cloudflare API token first.");

        try
        {
            await cloudflare.VerifyTokenAsync(trimmed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A workspace's Cloudflare token could not be verified.");
            return new CustomerDnsOutcome(false, ex.Message);
        }

        var row = await db.CustomerDnsCredentials.FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
        if (row is null)
        {
            row = new CustomerDnsCredential { WorkspaceId = workspaceId };
            db.CustomerDnsCredentials.Add(row);
        }
        row.EncryptedToken = protector.Protect(trimmed);
        row.LastVerifiedAt = clock.UtcNow;
        row.LastVerificationError = null;
        await db.SaveChangesAsync(ct);

        return new CustomerDnsOutcome(true, "The Cloudflare token was verified and saved.");
    }

    public async Task<CustomerDnsOutcome> RemoveTokenAsync(Guid workspaceId, CancellationToken ct)
    {
        var row = await db.CustomerDnsCredentials.FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
        if (row is null) return new CustomerDnsOutcome(true, "No token was stored.");

        db.CustomerDnsCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
        return new CustomerDnsOutcome(true, "The Cloudflare token was removed.");
    }

    /// <summary>
    /// Every zone the token can see, fetched live. On success or failure the credential's own
    /// verification state is updated to match — a page loaded later reads back what this call
    /// found rather than a stale guess.
    /// </summary>
    public async Task<CustomerDnsZonesResult> ListZonesAsync(Guid workspaceId, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(workspaceId, ct);
        if (token is null) return CustomerDnsZonesResult.Fail("No Cloudflare token is set for this workspace.");

        try
        {
            var zones = await cloudflare.ListZonesAsync(token, ct);
            await MarkAsync(workspaceId, error: null, ct);
            return new CustomerDnsZonesResult(true, zones, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkAsync(workspaceId, ex.Message, ct);
            return CustomerDnsZonesResult.Fail(ex.Message);
        }
    }

    public async Task<CustomerDnsRecordsResult> ListRecordsAsync(Guid workspaceId, string zoneId, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(workspaceId, ct);
        if (token is null) return CustomerDnsRecordsResult.Fail("No Cloudflare token is set for this workspace.");

        try
        {
            var records = await cloudflare.ListDnsRecordsAsync(token, zoneId, ct);
            var supported = records.Where(r => SupportedTypes.Contains(r.Type)).ToList();
            return new CustomerDnsRecordsResult(true, supported, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CustomerDnsRecordsResult.Fail(ex.Message);
        }
    }

    public async Task<CustomerDnsOutcome> CreateRecordAsync(
        Guid workspaceId, string zoneId, string type, string name, string content, int ttl, int? priority,
        CancellationToken ct)
    {
        if (!SupportedTypes.Contains(type ?? ""))
            return new CustomerDnsOutcome(false,
                $"{type} records are not managed here. Supported types: {string.Join(", ", SupportedTypes)}.");

        var token = await ResolveTokenAsync(workspaceId, ct);
        if (token is null) return new CustomerDnsOutcome(false, "No Cloudflare token is set for this workspace.");

        try
        {
            await cloudflare.CreateDnsRecordAsync(token, zoneId, type!, name, content, ttl, priority, ct);
            return new CustomerDnsOutcome(true, "The DNS record was created.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CustomerDnsOutcome(false, ex.Message);
        }
    }

    public async Task<CustomerDnsOutcome> DeleteRecordAsync(
        Guid workspaceId, string zoneId, string recordId, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(workspaceId, ct);
        if (token is null) return new CustomerDnsOutcome(false, "No Cloudflare token is set for this workspace.");

        try
        {
            await cloudflare.DeleteDnsRecordAsync(token, zoneId, recordId, ct);
            return new CustomerDnsOutcome(true, "The DNS record was deleted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CustomerDnsOutcome(false, ex.Message);
        }
    }

    /// <summary>
    /// The decrypted token for exactly this workspace, or null when it has none. The explicit
    /// <c>WorkspaceId ==</c> here is deliberate belt-and-braces over the entity's own query filter —
    /// see the class remarks — so a caller that ever runs this unscoped (a background job, a future
    /// admin tool with <c>IgnoreQueryFilters()</c>) still cannot resolve the wrong workspace's token.
    /// </summary>
    private async Task<string?> ResolveTokenAsync(Guid workspaceId, CancellationToken ct)
    {
        var row = await db.CustomerDnsCredentials
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
        if (row is null) return null;
        try { return protector.Unprotect(row.EncryptedToken); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A workspace's stored Cloudflare token could not be decrypted.");
            return null;
        }
    }

    private async Task MarkAsync(Guid workspaceId, string? error, CancellationToken ct)
    {
        var row = await db.CustomerDnsCredentials.FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
        if (row is null) return; // the token was removed mid-call; nothing to record it against

        row.LastVerificationError = error;
        if (error is null) row.LastVerifiedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
