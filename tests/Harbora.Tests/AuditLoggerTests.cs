using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>Audit trail for privileged actions (doc 10 §2.13).</summary>
public class AuditLoggerTests
{
    private sealed class StubUser : ICurrentUser
    {
        public Guid? UserId { get; init; }
        public string? Email { get; init; }
        public bool IsAuthenticated => UserId is not null;
        public Guid? WorkspaceId { get; init; }
    }

    /// <summary>A request with a platform administrator behind the customer's account.</summary>
    private sealed class StubSupport : ISupportSession
    {
        public Guid? SessionId { get; init; }
        public Guid? AdminUserId { get; init; }
        public string? AdminEmail { get; init; }
    }

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("audit-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task Writes_an_entry_using_current_user_by_default()
    {
        using var db = NewDb();
        var uid = Guid.NewGuid();
        var log = new AuditLogger(db, new StubUser { UserId = uid, Email = "owner@harbora.local" },
            new Clock(), NoSupportSession.Instance, NullLogger<AuditLogger>.Instance);

        await log.LogAsync("app.deploy", "app", "app-123", ipAddress: "203.0.113.5");

        var row = db.AuditLogs.Single();
        row.Action.Should().Be("app.deploy");
        row.TargetType.Should().Be("app");
        row.TargetId.Should().Be("app-123");
        row.IpAddress.Should().Be("203.0.113.5");
        row.ActorEmail.Should().Be("owner@harbora.local");
        row.UserId.Should().Be(uid);
    }

    [Fact]
    public async Task Honors_actor_override_for_anonymous_events_like_failed_login()
    {
        using var db = NewDb();
        var log = new AuditLogger(db, new StubUser(), new Clock(), NoSupportSession.Instance,
            NullLogger<AuditLogger>.Instance);

        await log.LogAsync("user.login_failed", "user", null, "203.0.113.9",
            actorEmailOverride: "attacker@evil.test");

        var row = db.AuditLogs.Single();
        row.Action.Should().Be("user.login_failed");
        row.ActorEmail.Should().Be("attacker@evil.test");
        row.UserId.Should().BeNull();
    }

    [Fact]
    public async Task An_ordinary_request_stamps_no_support_session_on_the_row()
    {
        using var db = NewDb();
        var log = new AuditLogger(db, new StubUser { UserId = Guid.NewGuid(), Email = "customer@shop.test" },
            new Clock(), NoSupportSession.Instance, NullLogger<AuditLogger>.Instance);

        await log.LogAsync("app.deploy");

        var row = db.AuditLogs.Single();
        row.Action.Should().Be("app.deploy", "nothing about an ordinary act changes");
        row.SupportSessionId.Should().BeNull();
        row.SupportAdminUserId.Should().BeNull();
    }

    [Fact]
    public async Task An_act_under_a_support_session_names_the_customer_and_the_administrator_at_once()
    {
        using var db = NewDb();
        var customer = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var session = Guid.NewGuid();

        var log = new AuditLogger(db,
            new StubUser { UserId = customer, Email = "customer@shop.test" },
            new Clock(),
            new StubSupport { SessionId = session, AdminUserId = admin, AdminEmail = "support@harbora.local" },
            NullLogger<AuditLogger>.Instance);

        await log.LogAsync("app.deploy", "app", "app-123");

        var row = db.AuditLogs.Single();
        // The request really did run as the customer, and every tenancy decision behind it believed
        // so. The row says both things rather than choosing one and being half wrong.
        row.UserId.Should().Be(customer);
        row.ActorEmail.Should().Be("customer@shop.test");
        row.SupportAdminUserId.Should().Be(admin);
        row.SupportSessionId.Should().Be(session);
    }

    [Fact]
    public async Task Every_action_under_a_support_session_is_prefixed_so_the_customer_can_find_it()
    {
        using var db = NewDb();
        var log = new AuditLogger(db, new StubUser { UserId = Guid.NewGuid(), Email = "customer@shop.test" },
            new Clock(),
            new StubSupport { SessionId = Guid.NewGuid(), AdminUserId = Guid.NewGuid() },
            NullLogger<AuditLogger>.Instance);

        await log.LogAsync("app.deploy");

        db.AuditLogs.Single().Action.Should().Be("support.app.deploy");
    }

    [Fact]
    public async Task An_action_that_already_names_itself_support_is_not_prefixed_twice()
    {
        using var db = NewDb();
        var log = new AuditLogger(db, new StubUser { UserId = Guid.NewGuid(), Email = "c@shop.test" },
            new Clock(),
            new StubSupport { SessionId = Guid.NewGuid(), AdminUserId = Guid.NewGuid() },
            NullLogger<AuditLogger>.Instance);

        await log.LogAsync("support.refused");

        db.AuditLogs.Single().Action.Should().Be("support.refused");
    }
}
