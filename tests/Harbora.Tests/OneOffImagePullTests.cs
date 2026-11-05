using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The launch proof's real Laravel project stopped at its release task with "pull access denied for
/// harbora/laravel-real": the one-off asked a registry for the image the deployment had built a moment
/// earlier, which exists on this host and nowhere else. Every test of a release task runs over a fake
/// engine, so none of them could see it. This is the real engine against a scripted client.
/// </summary>
public class OneOffImagePullTests
{
    private sealed class Daemon
    {
        public List<string> Pulled { get; } = [];
        public List<string> Started { get; } = [];
    }

    private static (DockerEngine Engine, Daemon Daemon) EngineOverDaemon(bool imageIsHere)
    {
        var daemon = new Daemon();

        var images = DispatchStandIn.Create<IImageOperations>((method, args) =>
        {
            switch (method.Name)
            {
                case "InspectImageAsync":
                    return imageIsHere
                        ? Task.FromResult(new ImageInspectResponse())
                        : throw new DockerImageNotFoundException(HttpStatusCode.NotFound, "no such image");
                case "CreateImageAsync":
                    daemon.Pulled.Add(((ImagesCreateParameters)args[0]!).FromImage);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method.Name);
            }
        });

        var containers = DispatchStandIn.Create<IContainerOperations>((method, args) =>
        {
            switch (method.Name)
            {
                case "CreateContainerAsync":
                    return Task.FromResult(new CreateContainerResponse { ID = "one-off" });
                case "StartContainerAsync":
                    daemon.Started.Add((string)args[0]!);
                    return Task.FromResult(true);
                case "WaitContainerAsync":
                    return Task.FromResult(new ContainerWaitResponse { StatusCode = 0 });
                case "RemoveContainerAsync":
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

    [Fact]
    public async Task An_image_built_on_this_host_is_run_without_asking_a_registry_for_it()
    {
        var (engine, daemon) = EngineOverDaemon(imageIsHere: true);

        var exit = await engine.RunOneOffAsync(
            new DockerOneOffRequest("harbora/laravel-real:build-1", ["sh", "-c", "php artisan migrate --force"], []), null, default);

        exit.Should().Be(0);
        daemon.Pulled.Should().BeEmpty();
        daemon.Started.Should().Equal("one-off");
    }

    [Fact]
    public async Task An_image_this_host_does_not_have_is_still_pulled_first()
    {
        // Every other one-off is a stock client image on a host that may never have run it.
        var (engine, daemon) = EngineOverDaemon(imageIsHere: false);

        await engine.RunOneOffAsync(new DockerOneOffRequest("postgres:16-alpine", ["pg_isready"], []), null, default);

        daemon.Pulled.Should().ContainSingle().Which.Should().Contain("postgres");
        daemon.Started.Should().Equal("one-off");
    }
}
