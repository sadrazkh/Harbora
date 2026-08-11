using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Mail;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Mail;

public sealed record MailOperationResult(bool Ok, string? Secret = null, string? Error = null);

public sealed class MailPlatformService(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IQuotaService quotas,
    ISecretProtector protector,
    StalwartClient stalwart,
    Harbora.Infrastructure.Billing.ResourceCreationBilling creationBilling)
{
    private static readonly Regex DomainPattern = new(
        "^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LocalPattern = new(
        "^[a-z0-9](?:[a-z0-9.!#$%&'*+/=?^_`{|}~-]{0,62}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<MailOperationResult> ProvisionAsync(
        Guid serverId, string hostname, string apiBaseUrl, string image,
        string adminUser, string? adminPassword, long? domainRate, long? mailboxRate,
        int maxDomains, int maxMailboxes, CancellationToken ct)
    {
        if (await db.MailServers.IgnoreQueryFilters().AnyAsync(x => x.IsActive, ct))
            return new(false, Error: "An active platform mail server already exists.");
        if (!DomainPattern.IsMatch(hostname.Trim().TrimEnd('.')))
            return new(false, Error: "Enter a valid public mail hostname.");
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var api)
            || api.Scheme is not ("http" or "https"))
            return new(false, Error: "The management API URL must be an absolute HTTP or HTTPS URL.");
        if (domainRate is null || domainRate < 0 || mailboxRate is null || mailboxRate < 0)
            return new(false, Error: "Both hourly prices must be set; zero means deliberately free.");

        var serverExists = await db.Servers.IgnoreQueryFilters().AnyAsync(s => s.Id == serverId, ct);
        if (!serverExists) return new(false, Error: "The selected server no longer exists.");

        var generated = string.IsNullOrWhiteSpace(adminPassword)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            : adminPassword;
        adminUser = string.IsNullOrWhiteSpace(adminUser) ? "admin" : adminUser.Trim();

        var row = new MailServer
        {
            ServerId = serverId,
            PublicHostname = hostname.Trim().TrimEnd('.').ToLowerInvariant(),
            ApiBaseUrl = apiBaseUrl.TrimEnd('/'),
            Image = string.IsNullOrWhiteSpace(image) ? "stalwartlabs/stalwart:v0.16" : image.Trim(),
            EncryptedAdminUser = protector.Protect(adminUser),
            EncryptedAdminPassword = protector.Protect(generated),
            DomainRatePerHourMinor = domainRate,
            MailboxRatePerHourMinor = mailboxRate,
            MaxDomainsPerWorkspace = Math.Max(0, maxDomains),
            MaxMailboxesPerWorkspace = Math.Max(0, maxMailboxes)
        };
        db.MailServers.Add(row);
        await db.SaveChangesAsync(ct);

        try
        {
            var docker = await engines.ResolveAsync(serverId, ct);
            await docker.PullImageAsync(row.Image, new Progress<string>(_ => { }), ct);
            await docker.EnsureNetworkAsync("harbora-mail", ct);
            await docker.EnsureVolumeAsync("harbora-mail-config", ct);
            await docker.EnsureVolumeAsync("harbora-mail-data", ct);
            await RunContainerAsync(docker, row, adminUser + ":" + generated, ct);

            // The container can take a moment to initialise. The operator can use Test/activate if
            // the first probe races startup; provisioning itself remains successful.
            row.Status = MailServerStatus.Provisioning;
            await db.SaveChangesAsync(ct);
            return new(true, Secret: generated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.Status = MailServerStatus.Failed;
            row.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            return new(false, Error: ex.Message);
        }
    }

    public async Task<MailOperationResult> CompleteSetupAsync(
        Guid id, string permanentUser, string permanentPassword, CancellationToken ct)
    {
        var server = await db.MailServers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (server is null) return new(false, Error: "Mail server not found.");
        if (string.IsNullOrWhiteSpace(permanentUser) || string.IsNullOrWhiteSpace(permanentPassword))
            return new(false, Error: "Enter the permanent administrator created by the Stalwart setup wizard.");

        var test = await stalwart.TestAsync(
            server.ApiBaseUrl, permanentUser.Trim(), permanentPassword, ct);
        if (!test.Succeeded)
        {
            server.Status = MailServerStatus.Failed;
            server.LastError = test.Error;
            await db.SaveChangesAsync(ct);
            return new(false, Error: test.Error);
        }

        var recovery = User(server) + ":" + Password(server);
        try
        {
            var docker = await engines.ResolveAsync(server.ServerId, ct);
            await docker.RemoveContainerAsync(server.ContainerName, force: true, ct);
            await RunContainerAsync(docker, server, recoveryAdmin: null, ct);
            server.EncryptedAdminUser = protector.Protect(permanentUser.Trim());
            server.EncryptedAdminPassword = protector.Protect(permanentPassword);
            server.Status = MailServerStatus.Ready;
            server.LastError = null;
            await db.SaveChangesAsync(ct);
            return new(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Restore the bootstrap container so the operator is not locked out by a failed
            // recreation. It stays visibly failed and can be retried after the port/runtime issue.
            try
            {
                var docker = await engines.ResolveAsync(server.ServerId, CancellationToken.None);
                await RunContainerAsync(docker, server, recovery, CancellationToken.None);
            }
            catch { }
            server.Status = MailServerStatus.Failed;
            server.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            return new(false, Error: ex.Message);
        }
    }

    public async Task<MailOperationResult> UpdateOfferAsync(
        Guid id, long? domainRate, long? mailboxRate, int maxDomains, int maxMailboxes, CancellationToken ct)
    {
        if (domainRate is null || domainRate < 0 || mailboxRate is null || mailboxRate < 0)
            return new(false, Error: "Both prices must be set and cannot be negative.");
        var server = await db.MailServers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (server is null) return new(false, Error: "Mail server not found.");
        server.DomainRatePerHourMinor = domainRate;
        server.MailboxRatePerHourMinor = mailboxRate;
        server.MaxDomainsPerWorkspace = Math.Max(0, maxDomains);
        server.MaxMailboxesPerWorkspace = Math.Max(0, maxMailboxes);
        await db.SaveChangesAsync(ct);
        return new(true);
    }

    public async Task<MailOperationResult> CreateDomainAsync(Guid workspaceId, string name, CancellationToken ct)
    {
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern.IsMatch(name)) return new(false, Error: "Enter a valid domain name.");
        await using var reservation = await quotas.AcquireCreationLockAsync(workspaceId, ct);
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        if (server.DomainRatePerHourMinor is not { } rate)
            return new(false, Error: "The provider has not priced email domains.");
        if (await db.MailDomains.IgnoreQueryFilters().AnyAsync(x => x.Domain == name, ct))
            return new(false, Error: "This mail domain is already registered.");
        var count = await db.MailDomains.CountAsync(x => x.WorkspaceId == workspaceId, ct);
        if (server.MaxDomainsPerWorkspace > 0 && count >= server.MaxDomainsPerWorkspace)
            return new(false, Error: $"This workspace has reached its {server.MaxDomainsPerWorkspace} mail-domain limit.");

        var remote = await stalwart.CreateDomainAsync(
            server.ApiBaseUrl, User(server), Password(server), name, ct);
        if (!remote.Succeeded) return new(false, Error: remote.Error);

        var row = new MailDomain
        {
            WorkspaceId = workspaceId, MailServerId = server.Id, Domain = name,
            ProviderObjectId = remote.Id, Status = MailResourceStatus.Ready, RatePerHourMinor = rate
        };
        if (remote.Id is not null)
        {
            var dns = await stalwart.GetDomainDnsAsync(
                server.ApiBaseUrl, User(server), Password(server), remote.Id, ct);
            if (dns.Succeeded) row.DnsZone = dns.Zone;
        }
        db.MailDomains.Add(row);
        try
        {
            await creationBilling.SaveAsync(workspaceId,
                [new(BilledResourceType.MailDomain, row.Id, name, null, rate)], ct);
            await reservation.CommitAsync(ct);
            return new(true);
        }
        catch
        {
            if (remote.Id is not null)
                await stalwart.DeleteDomainAsync(server.ApiBaseUrl, User(server), Password(server), remote.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<MailOperationResult> CreateMailboxAsync(
        Guid workspaceId, Guid domainId, string localPart, string displayName, long quotaMb, CancellationToken ct)
    {
        localPart = localPart.Trim().ToLowerInvariant();
        if (!LocalPattern.IsMatch(localPart)) return new(false, Error: "Enter a valid mailbox name.");
        await using var reservation = await quotas.AcquireCreationLockAsync(workspaceId, ct);
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        if (server.MailboxRatePerHourMinor is not { } rate)
            return new(false, Error: "The provider has not priced mailboxes.");
        var domain = await db.MailDomains.FirstOrDefaultAsync(
            d => d.Id == domainId && d.WorkspaceId == workspaceId && d.Status == MailResourceStatus.Ready, ct);
        if (domain?.ProviderObjectId is null) return new(false, Error: "Mail domain not found or not ready.");
        if (await db.MailMailboxes.AnyAsync(m => m.MailDomainId == domainId && m.LocalPart == localPart, ct))
            return new(false, Error: "This mailbox already exists.");
        var count = await db.MailMailboxes.CountAsync(x => x.WorkspaceId == workspaceId, ct);
        if (server.MaxMailboxesPerWorkspace > 0 && count >= server.MaxMailboxesPerWorkspace)
            return new(false, Error: $"This workspace has reached its {server.MaxMailboxesPerWorkspace} mailbox limit.");

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var quotaBytes = quotaMb <= 0 ? 0 : checked(quotaMb * 1024 * 1024);
        var remote = await stalwart.CreateMailboxAsync(
            server.ApiBaseUrl, User(server), Password(server), domain.ProviderObjectId,
            localPart, password, displayName.Trim(), quotaBytes, ct);
        if (!remote.Succeeded) return new(false, Error: remote.Error);

        var row = new MailMailbox
        {
            WorkspaceId = workspaceId, MailDomainId = domain.Id, LocalPart = localPart,
            DisplayName = displayName.Trim(), ProviderObjectId = remote.Id,
            Status = MailResourceStatus.Ready, RatePerHourMinor = rate, QuotaBytes = quotaBytes
        };
        db.MailMailboxes.Add(row);
        try
        {
            await creationBilling.SaveAsync(workspaceId,
                [new(BilledResourceType.Mailbox, row.Id, localPart + "@" + domain.Domain, null, rate)], ct);
            await reservation.CommitAsync(ct);
            return new(true, Secret: password);
        }
        catch
        {
            if (remote.Id is not null)
                await stalwart.DeleteMailboxAsync(server.ApiBaseUrl, User(server), Password(server), remote.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<MailOperationResult> ResetMailboxPasswordAsync(
        Guid workspaceId, Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.MailMailboxes.Include(x => x.MailDomain)
            .FirstOrDefaultAsync(x => x.Id == mailboxId && x.WorkspaceId == workspaceId, ct);
        if (mailbox?.ProviderObjectId is null) return new(false, Error: "Mailbox not found or not ready.");
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var remote = await stalwart.ResetMailboxPasswordAsync(
            server.ApiBaseUrl, User(server), Password(server), mailbox.ProviderObjectId, password, ct);
        return remote.Succeeded ? new(true, Secret: password) : new(false, Error: remote.Error);
    }

    public async Task<MailOperationResult> DeleteMailboxAsync(
        Guid workspaceId, Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.MailMailboxes.FirstOrDefaultAsync(
            x => x.Id == mailboxId && x.WorkspaceId == workspaceId, ct);
        if (mailbox is null) return new(false, Error: "Mailbox not found.");
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        if (mailbox.ProviderObjectId is not null)
        {
            var remote = await stalwart.DeleteMailboxAsync(
                server.ApiBaseUrl, User(server), Password(server), mailbox.ProviderObjectId, ct);
            if (!remote.Succeeded) return new(false, Error: remote.Error);
        }
        db.MailMailboxes.Remove(mailbox);
        await db.SaveChangesAsync(ct);
        return new(true);
    }

    public async Task<MailOperationResult> DeleteDomainAsync(
        Guid workspaceId, Guid domainId, string confirmation, CancellationToken ct)
    {
        var domain = await db.MailDomains.Include(x => x.Mailboxes).FirstOrDefaultAsync(
            x => x.Id == domainId && x.WorkspaceId == workspaceId, ct);
        if (domain is null) return new(false, Error: "Mail domain not found.");
        if (!string.Equals(domain.Domain, confirmation.Trim(), StringComparison.OrdinalIgnoreCase))
            return new(false, Error: "Type the full domain name to confirm deletion.");
        if (domain.Mailboxes.Count > 0)
            return new(false, Error: "Delete every mailbox on this domain first.");
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        if (domain.ProviderObjectId is not null)
        {
            var remote = await stalwart.DeleteDomainAsync(
                server.ApiBaseUrl, User(server), Password(server), domain.ProviderObjectId, ct);
            if (!remote.Succeeded) return new(false, Error: remote.Error);
        }
        db.MailDomains.Remove(domain);
        await db.SaveChangesAsync(ct);
        return new(true);
    }

    public async Task<MailOperationResult> RefreshDnsAsync(
        Guid workspaceId, Guid domainId, CancellationToken ct)
    {
        var domain = await db.MailDomains.FirstOrDefaultAsync(
            x => x.Id == domainId && x.WorkspaceId == workspaceId, ct);
        if (domain?.ProviderObjectId is null) return new(false, Error: "Mail domain not found or not ready.");
        var server = await ReadyServerAsync(ct);
        if (server is null) return new(false, Error: "The platform mail server is not ready.");
        var dns = await stalwart.GetDomainDnsAsync(
            server.ApiBaseUrl, User(server), Password(server), domain.ProviderObjectId, ct);
        if (!dns.Succeeded) return new(false, Error: dns.Error);
        domain.DnsZone = dns.Zone;
        await db.SaveChangesAsync(ct);
        return new(true);
    }

    private Task<MailServer?> ReadyServerAsync(CancellationToken ct) =>
        db.MailServers.IgnoreQueryFilters().FirstOrDefaultAsync(
            x => x.IsActive && x.Status == MailServerStatus.Ready, ct);
    private string User(MailServer server) => protector.Unprotect(server.EncryptedAdminUser);
    private string Password(MailServer server) => protector.Unprotect(server.EncryptedAdminPassword);

    private static Task<string> RunContainerAsync(
        IDockerEngine docker, MailServer row, string? recoveryAdmin, CancellationToken ct)
    {
        var env = new Dictionary<string, string> { ["STALWART_PUBLIC_URL"] = row.ApiBaseUrl };
        if (!string.IsNullOrEmpty(recoveryAdmin)) env["STALWART_RECOVERY_ADMIN"] = recoveryAdmin;
        return docker.RunContainerAsync(new DockerRunRequest(
            row.Image, row.ContainerName, "harbora-mail", env,
            new Dictionary<string, string> { ["harbora.platform-service"] = "mail" },
            [
                ("harbora-mail-config", "/etc/stalwart", false),
                ("harbora-mail-data", "/var/lib/stalwart", false)
            ],
            ContainerPort: 8080,
            MemoryLimitBytes: 1024L * 1024 * 1024,
            CpuLimit: 1,
            HealthCheckPath: null,
            PublishToHostPort: 8080,
            AdditionalPublishedPorts: new Dictionary<int, int>
            {
                [25] = 25, [465] = 465, [587] = 587, [993] = 993
            }), ct);
    }
}
