using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one promise <see cref="Harbora.Application.Abstractions.IServerEngineFactory.Local"/> makes:
/// it is the same object <c>ResolveAsync</c> returns for this machine, by reference.
///
/// <para>
/// Two callers now answer "is this work happening here?" by comparing what they resolved against
/// that property — the external-access gateway, which publishes a port on this host, and the backup
/// engine, whose helper has to share this panel's staging volume. Both of them refuse when the
/// answer is no. So a factory that started returning a fresh engine for the local server would not
/// throw, would not log, and would not fail a single test that only exercises those callers through
/// a fake: it would simply tell every single-server install that its own databases and volumes are
/// somewhere else. Which is why this is asserted against the <em>real</em> factory.
/// </para>
/// </summary>
public sealed class ServerEngineIdentityTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("engine-identity-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();

    /// <summary>
    /// Only the local branch is exercised here, and it returns before anything node-shaped or
    /// HTTP-shaped is touched — so those collaborators are deliberately absent rather than mocked. If
    /// the local branch ever starts reaching for one, these tests will say so by throwing.
    /// </summary>
    private ServerEngineFactory Factory() => new(
        _panel, _db, null!, null!, null!, null!, null!,
        NullLogger<NodeWorkloadEngine>.Instance, NullLogger<ServerEngineFactory>.Instance);

    [Fact]
    public async Task The_local_server_resolves_to_the_very_engine_offered_as_Local()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "this panel", IsLocal = true };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();

        var factory = Factory();

        var resolved = await factory.ResolveAsync(server.Id, default);

        resolved.Should().BeSameAs(factory.Local,
            "callers compare by reference to decide whether the work is happening on this machine");
    }

    /// <summary>
    /// An app or service can carry a server id nothing has a row for — one created before servers
    /// existed carries <c>Guid.Empty</c>. The factory calls that this machine, and it has to be this
    /// machine's engine by the same reference, or every such resource looks remote.
    /// </summary>
    [Fact]
    public async Task A_server_nobody_has_a_row_for_resolves_to_that_same_engine()
    {
        var factory = Factory();

        var resolved = await factory.ResolveAsync(Guid.Empty, default);

        resolved.Should().BeSameAs(factory.Local);
    }

    public void Dispose() => _db.Dispose();
}
