using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests.Verification;

/// <summary>
/// A connection test is a one-off container with a database password in its environment, and the
/// checks put a timeout on it. So cancelling one has to remove it, not leave it behind on the host.
/// The removal used to be sent with the token that had just been cancelled, which never reached the
/// daemon. This is the real engine against a scripted client; what a real daemon does with the removal
/// is not something this can show.
/// </summary>
public class OneOffCleanupTests
{
    private sealed class Daemon
    {
        public List<bool> RemovalsSentWithADeadToken { get; } = [];
        public List<string> Removed { get; } = [];
        public TaskCompletionSource WaitStarted { get; } = new();
    }

    private static (DockerEngine Engine, Daemon Daemon) EngineOverDaemon(Func<CancellationToken, Task<ContainerWaitResponse>> wait)
    {
        var daemon = new Daemon();

        var images = DispatchStandIn.Create<IImageOperations>((method, _) => method.Name == "CreateImageAsync"
            ? Task.CompletedTask
            : throw new NotSupportedException(method.Name));

        var containers = DispatchStandIn.Create<IContainerOperations>((method, args) =>
        {
            var ct = args.OfType<CancellationToken>().SingleOrDefault();
            switch (method.Name)
            {
                case "CreateContainerAsync":
                    return Task.FromResult(new CreateContainerResponse { ID = "one-off" });
                case "StartContainerAsync":
                    return Task.FromResult(true);
                case "WaitContainerAsync":
                    daemon.WaitStarted.TrySetResult();
                    return wait(ct);
                case "RemoveContainerAsync":
                    daemon.RemovalsSentWithADeadToken.Add(ct.IsCancellationRequested);
                    if (!ct.IsCancellationRequested) daemon.Removed.Add((string)args[0]!);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method.Name);
            }
        });

        var client = DispatchStandIn.Create<IDockerClient>((method, _) => method.Name switch
        {
            "get_Images" => images,
            "get_Containers" => containers,
            _ => throw new NotSupportedException(method.Name)
        });

        return (new DockerEngine(client, NullLogger<DockerEngine>.Instance), daemon);
    }

    private static DockerOneOffRequest Probe() => new("postgres:16", ["sh", "-c", "psql"], [],
        Env: new Dictionary<string, string> { ["PGPASSWORD"] = "db-password" }, NetworkMode: "harbora-env-x");

    [Fact]
    public async Task A_one_off_stopped_by_a_timeout_is_still_removed()
    {
        var (engine, daemon) = EngineOverDaemon(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ContainerWaitResponse();
        });

        using var timeout = new CancellationTokenSource();
        var run = engine.RunOneOffAsync(Probe(), null, timeout.Token);
        await daemon.WaitStarted.Task;
        await timeout.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();

        daemon.Removed.Should().Equal("one-off");
        daemon.RemovalsSentWithADeadToken.Should().Equal(false);
    }

    [Fact]
    public async Task A_one_off_that_finished_is_removed_as_it_always_was()
    {
        var (engine, daemon) = EngineOverDaemon(_ => Task.FromResult(new ContainerWaitResponse { StatusCode = 3 }));

        var exit = await engine.RunOneOffAsync(Probe(), null, default);

        exit.Should().Be(3);
        daemon.Removed.Should().Equal("one-off");
    }
}
