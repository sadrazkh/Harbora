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
            new Clock(), NullLogger<AuditLogger>.Instance);

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
        var log = new AuditLogger(db, new StubUser(), new Clock(), NullLogger<AuditLogger>.Instance);

        await log.LogAsync("user.login_failed", "user", null, "203.0.113.9",
            actorEmailOverride: "attacker@evil.test");

        var row = db.AuditLogs.Single();
        row.Action.Should().Be("user.login_failed");
        row.ActorEmail.Should().Be("attacker@evil.test");
        row.UserId.Should().BeNull();
    }
}
