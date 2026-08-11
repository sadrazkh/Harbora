using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

public sealed class ArtifactRelayRegistryTests
{
    [Fact]
    public void A_ticket_is_direction_bound_one_use_and_a_wrong_token_does_not_consume_it()
    {
        var registry = new ArtifactRelayRegistry(TimeProvider.System);
        var ticket = registry.CreateUpload(Path.Combine(Path.GetTempPath(), "artifact.tgz"));

        registry.TryConsume(ticket.Id, "wrong", ArtifactRelayDirection.UploadToPanel, out _).Should().BeFalse();
        registry.TryConsume(ticket.Id, ticket.Token, ArtifactRelayDirection.DownloadFromPanel, out _).Should().BeFalse();
        registry.TryConsume(ticket.Id, ticket.Token, ArtifactRelayDirection.UploadToPanel, out var lease).Should().BeTrue();
        lease!.Path.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "artifact.tgz")));
        registry.TryConsume(ticket.Id, ticket.Token, ArtifactRelayDirection.UploadToPanel, out _).Should().BeFalse();
    }

    [Fact]
    public void Expired_tickets_are_refused()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-11T00:00:00Z"));
        var registry = new ArtifactRelayRegistry(clock);
        var ticket = registry.CreateDownload(Path.Combine(Path.GetTempPath(), "artifact.tgz"));

        clock.Advance(TimeSpan.FromHours(2));

        registry.TryConsume(ticket.Id, ticket.Token, ArtifactRelayDirection.DownloadFromPanel, out _).Should().BeFalse();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
