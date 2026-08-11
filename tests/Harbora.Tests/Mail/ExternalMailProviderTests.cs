using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Mail;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Common;
using Harbora.Infrastructure.Mail;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Mail;

public sealed class ExternalMailProviderTests
{
    [Fact]
    public async Task Managed_provisioning_publishes_secure_mail_ports_and_public_discovery_url()
    {
        await using var db = Database();
        var host = new Server { Name = "mail-node", Hostname = "203.0.113.10" };
        db.Servers.Add(host);
        await db.SaveChangesAsync();
        var docker = new FakeDockerEngine();
        var service = Service(db, docker);

        var result = await service.ProvisionAsync(
            host.Id, "mail.example.com", "http://203.0.113.10:8080",
            "stalwartlabs/stalwart:v0.16", "admin", "secret", 100, 200, 5, 25, default);

        result.Ok.Should().BeTrue(result.Error);
        var run = docker.RunRequests.Should().ContainSingle().Subject;
        run.Env["STALWART_PUBLIC_URL"].Should().Be("https://mail.example.com");
        run.AdditionalPublishedPorts.Should().Contain(new Dictionary<int, int>
        {
            [25] = 25, [443] = 443, [465] = 465, [587] = 587, [993] = 993
        });
        run.AdditionalPublishedPorts.Should().NotContainKeys(110, 143);
    }

    [Fact]
    public async Task External_domain_persists_provider_dns_and_client_settings_without_billing()
    {
        await using var db = Database();
        var workspaceId = Guid.NewGuid();
        var service = Service(db);

        var result = await service.CreateExternalDomainAsync(
            workspaceId, "Example.COM", "Zoho", "mx.zoho.com", 10,
            "v=spf1 include:zoho.com -all", "zmail._domainkey", "v=DKIM1; p=abc",
            "v=DMARC1; p=quarantine", "imap.zoho.com", 993,
            "smtp.zoho.com", 465, "https://mailadmin.zoho.com",
            "20 mx2.zoho.com.\nverify.example.com. TXT provider-code", default);

        result.Ok.Should().BeTrue(result.Error);
        var domain = await db.MailDomains.SingleAsync();
        domain.Mode.Should().Be(MailDomainMode.External);
        domain.MailServerId.Should().BeNull();
        domain.RatePerHourMinor.Should().Be(0);
        domain.Domain.Should().Be("example.com");
        domain.DnsZone.Should().Contain("example.com. 3600 IN MX 10 mx.zoho.com.");
        domain.DnsZone.Should().Contain("zmail._domainkey.example.com. 3600 IN TXT");
        domain.DnsZone.Should().Contain("20 mx2.zoho.com.");
        domain.ExternalImapHost.Should().Be("imap.zoho.com");
        domain.ExternalSmtpPort.Should().Be(465);
    }

    [Fact]
    public async Task Dkim_owner_cannot_inject_an_extra_zone_record()
    {
        await using var db = Database();
        var result = await Service(db).CreateExternalDomainAsync(
            Guid.NewGuid(), "example.com", "Provider", "mx.provider.com", 10,
            null, "selector._domainkey evil.example", "v=DKIM1; p=abc", null,
            null, null, null, null, null, null, default);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("valid DKIM record name");
        (await db.MailDomains.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_an_external_domain_does_not_require_a_managed_mail_server()
    {
        await using var db = Database();
        var workspaceId = Guid.NewGuid();
        var domain = new MailDomain
        {
            WorkspaceId = workspaceId,
            Mode = MailDomainMode.External,
            Domain = "example.com",
            ExternalProviderName = "Provider",
            Status = MailResourceStatus.Ready
        };
        db.MailDomains.Add(domain);
        await db.SaveChangesAsync();

        var result = await Service(db).DeleteDomainAsync(workspaceId, domain.Id, "example.com", default);

        result.Ok.Should().BeTrue(result.Error);
        (await db.MailDomains.CountAsync()).Should().Be(0);
    }

    private static HarboraDbContext Database() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("external-mail-" + Guid.NewGuid()).Options);

    private static MailPlatformService Service(HarboraDbContext db, FakeDockerEngine? docker = null)
    {
        docker ??= new FakeDockerEngine();
        var billing = new ResourceCreationBilling(
            db, new SystemClock(), Options.Create(new BillingOptions { Enabled = false }));
        return new MailPlatformService(
            db, new FakeServerEngineFactory(docker), new AllowQuota(), new PassthroughProtector(),
            new StalwartClient(new NoHttpFactory()), billing);
    }

    private sealed class AllowQuota : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? size, Guid? excluded, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, string? size, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("External providers use no HTTP API.");
    }
}
