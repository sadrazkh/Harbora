using System.Reflection;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the REAL <see cref="DockerEngine"/> asks the daemon for when it is told to look at, or prune,
/// dangling images.
///
/// <para>
/// This is the safety line of the whole feature, so it is pinned at the one place it can be: the
/// request that leaves the engine. The daemon's prune API defaults to <c>dangling=true</c> when the
/// filter is absent and to <c>prune -a</c> — every unused TAGGED image, build bases and rollback tags
/// included — when it says <c>dangling=false</c>. The engine must therefore send the filter, explicitly,
/// with <c>true</c>, and must never fall back to listing images and removing them by name.
/// </para>
///
/// <para>
/// <b>What this is not.</b> There is no Docker on the machine that runs this suite. The daemon's
/// response is scripted, so what Docker actually removes for that request is not exercised here — it is
/// Docker's documented behaviour, and the manual <c>docker image prune -f</c> on the live server
/// (165 dangling images, 69.78 GB reclaimed, all 13 running containers unaffected) is the one real
/// observation this project has of it. The client is a <see cref="DispatchProxy"/> rather than a
/// hand-written implementation of Docker.DotNet's interfaces, so it cannot drift when that library adds
/// a member.
/// </para>
/// </summary>
public sealed class DockerEngineDanglingTests
{
    /// <summary>Stands in for <see cref="IImageOperations"/>: answers the two calls under test with what
    /// the test scripted, records every call it received, and refuses everything else by name — so a
    /// change that reached for <c>DeleteImageAsync</c> fails loudly instead of silently working.</summary>
    public class ImageOperationsProxy : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public ImagesPruneParameters? Pruned { get; private set; }
        public ImagesListParameters? Listed { get; private set; }
        public ImagesPruneResponse PruneAnswer { get; set; } = new();
        public IList<ImagesListResponse> ListAnswer { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            Calls.Add(name);

            switch (name)
            {
                case nameof(IImageOperations.PruneImagesAsync):
                    Pruned = (ImagesPruneParameters)args![0]!;
                    return Task.FromResult(PruneAnswer);
                case nameof(IImageOperations.ListImagesAsync):
                    Listed = (ImagesListParameters)args![0]!;
                    return Task.FromResult(ListAnswer);
                default:
                    throw new NotSupportedException(
                        $"{name} is not part of what the dangling-image calls may use.");
            }
        }
    }

    /// <summary>Stands in for <see cref="IDockerClient"/>: only <c>Images</c> answers.</summary>
    public class ClientProxy : DispatchProxy
    {
        public IImageOperations? Images { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "get_" + nameof(IDockerClient.Images)
                ? Images
                : throw new NotSupportedException($"{targetMethod.Name} is not used by the dangling-image calls.");
    }

    private static (DockerEngine Engine, ImageOperationsProxy Daemon) Engine()
    {
        var images = DispatchProxy.Create<IImageOperations, ImageOperationsProxy>();
        var client = DispatchProxy.Create<IDockerClient, ClientProxy>();
        ((ClientProxy)(object)client).Images = images;

        return (new DockerEngine(client, NullLogger<DockerEngine>.Instance), (ImageOperationsProxy)(object)images);
    }

    // ---- the request that leaves the engine ----

    [Fact]
    public async Task The_prune_asks_the_daemon_for_dangling_true_and_nothing_else()
    {
        var (engine, daemon) = Engine();

        await engine.PruneDanglingImagesAsync(default);

        daemon.Pruned.Should().NotBeNull();
        var filters = daemon.Pruned!.Filters;
        filters.Should().NotBeNull();
        filters.Keys.Should().Equal(new[] { "dangling" }, "no other filter, and above all not an 'until' or a label that widens it");
        filters["dangling"].Should().Equal(
            new Dictionary<string, bool> { ["true"] = true },
            "'dangling=false' is prune -a: every unused tagged image, build bases and rollback tags included");
    }

    [Fact]
    public async Task The_prune_is_one_daemon_call_and_never_a_list_and_remove_loop()
    {
        var (engine, daemon) = Engine();
        // A stale listing of images that a hand-rolled loop would have gone on to remove one by one.
        daemon.ListAnswer = [new ImagesListResponse { ID = "sha256:stale", Size = 10 }];

        await engine.PruneDanglingImagesAsync(default);

        daemon.Calls.Should().Equal(new[] { nameof(IImageOperations.PruneImagesAsync) },
            "the daemon decides what is dangling at the instant it prunes; a loop over a listing would " +
            "reintroduce the race that call already handles — and any removal by name is off the table");
    }

    [Fact]
    public void Neither_the_list_nor_the_prune_can_ever_send_dangling_false()
    {
        foreach (var filters in new[] { DockerEngine.DanglingListParameters().Filters, DockerEngine.DanglingPruneParameters().Filters })
        {
            filters.Should().ContainKey("dangling");
            filters["dangling"].Should().ContainKey("true").WhoseValue.Should().BeTrue();
            filters["dangling"].Should().NotContainKey("false");
            filters.Should().HaveCount(1);
        }
    }

    // ---- what comes back ----

    [Fact]
    public async Task The_daemons_own_figures_are_reported_and_an_untag_only_entry_is_not_a_deletion()
    {
        var (engine, daemon) = Engine();
        daemon.PruneAnswer = new ImagesPruneResponse
        {
            ImagesDeleted =
            [
                new ImageDeleteResponse { Deleted = "sha256:aaa" },
                new ImageDeleteResponse { Untagged = "old@sha256:bbb" },
                new ImageDeleteResponse { Deleted = "sha256:ccc" },
            ],
            SpaceReclaimed = 74_920_000_000,
        };

        var pruned = await engine.PruneDanglingImagesAsync(default);

        pruned.ImagesDeleted.Should().Be(2);
        pruned.BytesReclaimed.Should().Be(74_920_000_000, "the daemon's own SpaceReclaimed, not a sum of anything here");
    }

    [Fact]
    public async Task A_prune_that_found_nothing_reports_zero_from_a_null_deleted_list()
    {
        // The daemon answers "ImagesDeleted": null when there was nothing to prune.
        var (engine, daemon) = Engine();
        daemon.PruneAnswer = new ImagesPruneResponse { ImagesDeleted = null!, SpaceReclaimed = 0 };

        var pruned = await engine.PruneDanglingImagesAsync(default);

        pruned.ImagesDeleted.Should().Be(0);
        pruned.BytesReclaimed.Should().Be(0);
    }

    [Fact]
    public async Task A_reclaimed_figure_too_large_for_a_signed_long_is_clamped_not_wrapped_negative()
    {
        var (engine, daemon) = Engine();
        daemon.PruneAnswer = new ImagesPruneResponse { SpaceReclaimed = ulong.MaxValue };

        var pruned = await engine.PruneDanglingImagesAsync(default);

        pruned.BytesReclaimed.Should().Be(long.MaxValue);
    }

    // ---- the read ----

    [Fact]
    public async Task The_read_lists_with_the_same_dangling_filter_and_sums_each_images_size()
    {
        var (engine, daemon) = Engine();
        daemon.ListAnswer =
        [
            new ImagesListResponse { ID = "sha256:1", Size = 1_000 },
            new ImagesListResponse { ID = "sha256:2", Size = 2_500 },
        ];

        var found = await engine.GetDanglingImagesAsync(default);

        found.Count.Should().Be(2);
        found.SizeBytes.Should().Be(3_500);

        daemon.Calls.Should().Equal(new[] { nameof(IImageOperations.ListImagesAsync) }, "a read removes nothing");
        daemon.Listed!.All.Should().Be(false);
        daemon.Listed.Filters.Should().ContainKey("dangling");
        daemon.Listed.Filters["dangling"].Should().Equal(new Dictionary<string, bool> { ["true"] = true },
            "the same filter the prune uses, so 'what is reported' and 'what would be pruned' are one set");
    }

    [Fact]
    public async Task No_dangling_images_reads_as_zero_and_zero()
    {
        var (engine, _) = Engine();

        var found = await engine.GetDanglingImagesAsync(default);

        found.Count.Should().Be(0);
        found.SizeBytes.Should().Be(0);
    }
}
