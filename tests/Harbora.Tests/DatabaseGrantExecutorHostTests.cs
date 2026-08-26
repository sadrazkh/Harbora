using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which machine a grant's one-off client runs on (HARBORA-0059).
///
/// <para>
/// Unlike <see cref="DatabaseGatewayHostTests"/> and the admin tool, a grant needs nothing published
/// back to this panel: the client and the database it statements both live on whichever server holds
/// the data, over a network that only exists there. So this never refuses a database merely for
/// being on another machine — it only has to run the client on the <em>right</em> one, and refuse by
/// name when that machine cannot be reached at all, or cannot do what was asked of it.
/// </para>
/// </summary>
public sealed class DatabaseGrantExecutorHostTests
{
    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;
    private readonly PassthroughProtector _protector = new();

    public DatabaseGrantExecutorHostTests() => _engines = new FakeServerEngineFactory(_panel);

    private DatabaseGrantExecutor Executor() =>
        new(_engines, _protector, NullLogger<DatabaseGrantExecutor>.Instance);

    private ManagedService Database(Guid serverId) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = Guid.CreateVersion7(),
        ServerId = serverId,
        Name = "orders",
        Type = ManagedServiceType.PostgreSql,
        ContainerName = "harbora-orders",
        DatabaseName = "orders",
        Username = "postgres",
        EncryptedPassword = _protector.Protect("admin_secret"),
        InternalPort = 5432
    };

    [Fact]
    public async Task A_database_on_this_machine_runs_the_client_locally()
    {
        var service = Database(Guid.Empty);

        var (ok, error) = await Executor().CreateAsync(service, "harbora-env-net", "tmp_user", "tmp_pass", default);

        ok.Should().BeTrue();
        error.Should().BeNull();
        _panel.Calls.Should().Contain(c => c.Operation == "RunOneOffAsync");
    }

    [Fact]
    public async Task A_database_on_another_reachable_server_runs_the_client_there_not_on_the_panel()
    {
        var serverId = Guid.NewGuid();
        var remote = new FakeDockerEngine();
        _engines.On(serverId, remote);
        var service = Database(serverId);

        var (ok, error) = await Executor().CreateAsync(service, "harbora-env-net", "tmp_user", "tmp_pass", default);

        ok.Should().BeTrue();
        error.Should().BeNull();
        remote.Calls.Should().Contain(c => c.Operation == "RunOneOffAsync",
            "the client and the database both live on the server that holds the data");
        _panel.Calls.Should().BeEmpty(
            "a grant needs nothing published back to this panel, so it must never run here instead");
    }

    [Fact]
    public async Task A_server_that_cannot_be_resolved_becomes_a_named_refusal_not_an_exception()
    {
        var serverId = Guid.NewGuid();
        _engines.Unreachable(serverId, "no agent endpoint and no node is enrolled on it");
        var service = Database(serverId);

        var (ok, error) = await Executor().CreateAsync(service, "harbora-env-net", "tmp_user", "tmp_pass", default);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("orders");
        error.Should().Contain("no agent endpoint");
        _panel.Calls.Should().BeEmpty("nothing was ever attempted once the server could not be reached");
    }

    /// <summary>
    /// A v1 node's command allowlist has no verb for running a one-off container to completion, by
    /// design (see <c>NodeWorkloadEngine</c>'s own class remarks) — so this is thrown locally, before
    /// the node is ever contacted, the moment a grant tries to reach a database placed on one. That
    /// makes it as certain a refusal as an undecryptable password, and it must say so by name rather
    /// than fall into the generic "lost contact" catch, which would send an operator chasing a flaky
    /// connection that was never the problem.
    /// </summary>
    [Fact]
    public async Task A_node_with_no_one_off_verb_is_refused_by_name_not_as_a_lost_connection()
    {
        var serverId = Guid.NewGuid();
        var node = new FakeDockerEngine
        {
            OneOffThrows = new NodeCapabilityException(
                "node-1", "run a one-off container",
                "A v1 node has no verb for running an arbitrary container to completion.")
        };
        _engines.On(serverId, node);
        var service = Database(serverId);

        var (ok, error) = await Executor().CreateAsync(service, "harbora-env-net", "tmp_user", "tmp_pass", default);

        ok.Should().BeFalse();
        error.Should().Contain("orders");
        error.Should().Contain("node-1");
        error.Should().NotContain("lost contact",
            "this is a known, certain incapability of the node — not an ambiguous dropped connection");
    }

    /// <summary>
    /// <c>RotateAsync</c> is the one caller that reads <c>Answered</c>, because it hands the returned
    /// password to a person on the strength of that flag. A node's missing one-off verb throws before
    /// the node is ever contacted, so it must count as answered — the same certainty an undecryptable
    /// password already gets — rather than the "may have landed" doubt a dropped connection leaves.
    /// </summary>
    [Fact]
    public async Task Rotation_against_a_one_off_incapable_node_is_answered_not_left_in_doubt()
    {
        var serverId = Guid.NewGuid();
        var node = new FakeDockerEngine
        {
            OneOffThrows = new NodeCapabilityException(
                "node-1", "run a one-off container", "No such verb exists.")
        };
        _engines.On(serverId, node);
        var service = Database(serverId);

        var (ok, error, answered) = await Executor().RotateAsync(
            service, "harbora-env-net", "tmp_user", "new_pass", default);

        ok.Should().BeFalse();
        answered.Should().BeTrue("nothing was ever risked — the node was never even contacted");
        error.Should().Contain("node-1");
    }
}
