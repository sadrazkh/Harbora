using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A build that does not produce its image must not be reported as built.
///
/// <para>
/// Loomi #9 and #12 on the live server: Docker 29's cache-restore error arrived as an ordinary
/// <c>stream</c> progress line, so nothing in the message stream said "failed". The build call
/// returned, the pipeline logged "Starting container", and the deployment died with
/// <c>No such image: harbora/loomi:build-12</c> — a symptom two steps removed from the cause.
/// <see cref="DockerEngine.BuildImageFromTarAsync"/> now checks the one fact that matters (does the tag
/// exist) after the stream ends, so it does not matter what shape the daemon's error took.
/// </para>
///
/// <para>
/// <b>What this proves and what it does not.</b> These tests drive the real
/// <c>BuildImageFromTarAsync</c> through Docker.DotNet's typed call, against a stand-in for
/// <c>IDockerClient</c> that reports the messages a test scripts and answers the image inspect a test
/// scripts. That exercises this engine's decision. It does not prove what a real Docker 29 daemon
/// streams for a failed cache restore — this machine has none — only that the engine does the right
/// thing for a stream that contains no error and an image that is not there.
/// </para>
/// </summary>
public class DockerEngineBuildImageExistsTests
{
    private const string Image = "harbora/loomi:build-12";
    private const string CacheRestoreLine =
        "failed to restore cached image from \"sha256:1d3c5e7a\" to sha256:9f8a7b21: failed to create cache image: x";

    private static JSONMessage Line(string text) => new() { Stream = text + "\n" };

    private static async Task<string> BuildAsync(ScriptedDockerClient daemon, List<string>? logged = null)
    {
        var engine = new DockerEngine(daemon.Client, NullLogger<DockerEngine>.Instance);
        var log = new Harbora.Infrastructure.Deployments.InlineProgress<string>(l => logged?.Add(l));
        using var tar = new MemoryStream([1, 2, 3]);

        return await engine.BuildImageFromTarAsync(
            tar, "Dockerfile", Image, new Dictionary<string, string>(), log, CancellationToken.None,
            cacheFrom: ["harbora/loomi:build-11"]);
    }

    [Fact]
    public async Task A_stream_that_reports_nothing_wrong_but_leaves_no_image_fails_naming_the_image_and_the_last_step()
    {
        var daemon = new ScriptedDockerClient(imageExists: false,
            Line("Step 18/23 : RUN npm ci"),
            Line("added 214 packages in 3s"),
            Line("Step 19/23 : COPY . ."),
            Line(CacheRestoreLine));

        var act = () => BuildAsync(daemon);

        var thrown = (await act.Should().ThrowAsync<DockerBuildException>()).Which;
        thrown.Message.Should().Contain(Image);
        thrown.Message.Should().Contain("The last step reported was Step 19/23 : COPY . .",
            "the last step seen is what the build was doing when it stopped");
        thrown.Message.Should().Contain("failed to restore cached image", "the daemon's last words are the reason");
        thrown.Message.Should().NotContain("No such image",
            "the point is to fail HERE, not two steps later with a symptom from someone else's call");
        daemon.InspectedImages.Should().Equal(Image);
    }

    [Fact]
    public async Task The_final_lines_of_the_stream_are_in_the_message_not_lost_to_a_late_callback()
    {
        // The last message is the one that matters most and the one Progress<T> is most likely to
        // deliver late, on the thread pool, after the build call has already returned.
        for (var run = 0; run < 25; run++)
        {
            var daemon = new ScriptedDockerClient(imageExists: false,
                Line("Step 3/3 : RUN make"), Line($"final line {run}"));

            var act = () => BuildAsync(daemon);

            (await act.Should().ThrowAsync<DockerBuildException>()).Which
                .Message.Should().Contain($"final line {run}");
        }
    }

    [Fact]
    public async Task Only_the_last_few_lines_are_quoted_not_the_whole_build_output()
    {
        var lines = Enumerable.Range(1, 60).Select(i => Line($"output line {i:D2}")).ToArray();
        var daemon = new ScriptedDockerClient(imageExists: false, lines);

        var act = () => BuildAsync(daemon);

        var message = (await act.Should().ThrowAsync<DockerBuildException>()).Which.Message;
        message.Should().Contain("output line 60");
        message.Should().Contain("output line 55");
        message.Should().NotContain("output line 40");
        message.Should().NotContain("output line 01");
    }

    [Fact]
    public async Task A_very_long_line_is_cut_rather_than_stored_whole()
    {
        var daemon = new ScriptedDockerClient(imageExists: false, Line(new string('x', 50_000)));

        var act = () => BuildAsync(daemon);

        var message = (await act.Should().ThrowAsync<DockerBuildException>()).Which.Message;
        message.Length.Should().BeLessThan(2_000);
    }

    [Fact]
    public async Task A_build_that_printed_no_step_at_all_still_names_the_image_and_says_no_step_was_seen()
    {
        var daemon = new ScriptedDockerClient(imageExists: false, Line("Sending build context to Docker daemon"));

        var act = () => BuildAsync(daemon);

        var message = (await act.Should().ThrowAsync<DockerBuildException>()).Which.Message;
        message.Should().Contain(Image);
        message.Should().Contain("No build step was reported");
        message.Should().Contain("Sending build context");
    }

    [Fact]
    public async Task A_build_that_printed_nothing_at_all_and_left_no_image_still_fails_with_the_image_named()
    {
        var daemon = new ScriptedDockerClient(imageExists: false);

        var act = () => BuildAsync(daemon);

        var message = (await act.Should().ThrowAsync<DockerBuildException>()).Which.Message;
        message.Should().Contain(Image);
        message.Should().Contain("printed nothing");
    }

    [Fact]
    public async Task A_build_that_succeeds_and_whose_tag_exists_is_unaffected()
    {
        var logged = new List<string>();
        var daemon = new ScriptedDockerClient(imageExists: true,
            Line("Step 1/2 : FROM node:20"), Line("Successfully built abc123"),
            Line($"Successfully tagged {Image}"));

        var built = await BuildAsync(daemon, logged);

        built.Should().Be(Image);
        logged.Should().Contain($"Successfully tagged {Image}");
        daemon.InspectedImages.Should().Equal(Image);
    }

    [Fact]
    public async Task A_failure_the_daemon_did_report_keeps_its_own_message_and_never_reaches_the_image_check()
    {
        // The existing detection is kept: this is a backstop for what it misses, not its replacement.
        var daemon = new ScriptedDockerClient(imageExists: true,
            Line("Step 6/23 : RUN npm run build"),
            new JSONMessage { Error = new JSONError { Message = "The command '/bin/sh -c npm run build' returned a non-zero code: 1" } });

        var act = () => BuildAsync(daemon);

        var message = (await act.Should().ThrowAsync<DockerBuildException>()).Which.Message;
        message.Should().Contain("failed at Step 6/23 : RUN npm run build");
        message.Should().Contain("returned a non-zero code: 1");
        message.Should().NotContain("does not exist");
        daemon.InspectedImages.Should().BeEmpty("the daemon already said the build failed; asking again adds nothing");
    }

    [Fact]
    public async Task A_daemon_that_cannot_be_asked_whether_the_image_exists_surfaces_that_error_not_a_false_success()
    {
        var daemon = new ScriptedDockerClient(imageExists: true, Line("Step 1/1 : FROM scratch"))
        {
            InspectFailure = new DockerApiException(HttpStatusCode.InternalServerError, "daemon is unhappy")
        };

        var act = () => BuildAsync(daemon);

        await act.Should().ThrowAsync<DockerApiException>();
    }

    // ---- the socket transport ----
    //
    // The live server talks to Docker over a Unix socket, where BuildImageFromTarAsync does not use
    // Docker.DotNet's build call at all (see DockerBuildTransport). The image check sits after both
    // branches, and these two tests hold it to that: the same scenario, sent through the socket.
    // The "daemon" is a script listening on a real Unix socket — the transport code is the real one,
    // the daemon behind it is not.

    private static async Task<string> BuildOverSocketAsync(ScriptedDockerClient client)
    {
        var engine = new DockerEngine(client.Client, NullLogger<DockerEngine>.Instance);
        using var tar = new MemoryStream([1, 2, 3]);

        return await engine.BuildImageFromTarAsync(
            tar, "Dockerfile", Image, new Dictionary<string, string>(),
            new Harbora.Infrastructure.Deployments.InlineProgress<string>(_ => { }), CancellationToken.None,
            cacheFrom: ["harbora/loomi:build-11"]);
    }

    [Fact]
    public async Task Over_the_socket_a_stream_that_reports_nothing_wrong_but_leaves_no_image_fails_the_same_way()
    {
        await using var daemon = new FakeUnixBuildDaemon(
            """{"stream":"Step 18/23 : RUN npm ci"}""",
            """{"stream":"Step 19/23 : COPY . ."}""",
            """{"stream":"failed to restore cached image from \"sha256:1d3c\" to sha256:9f8a: failed to create cache image: x"}""");
        var client = ScriptedDockerClient.OnSocket(daemon.Endpoint, imageExists: false);

        var act = () => BuildOverSocketAsync(client);

        var thrown = (await act.Should().ThrowAsync<DockerBuildException>()).Which;
        daemon.ServeFailure.Should().BeNull();
        daemon.RequestLine.Should().StartWith("POST /v1.41/build?");
        client.TypedBuildCalls.Should().Be(0, "this build must have gone through the socket transport, not the typed call");
        thrown.Message.Should().Contain(Image);
        thrown.Message.Should().Contain("The last step reported was Step 19/23 : COPY . .");
        thrown.Message.Should().Contain("failed to restore cached image");
        client.InspectedImages.Should().Equal(Image);
    }

    [Fact]
    public async Task Over_the_socket_a_build_that_succeeds_and_whose_tag_exists_is_unaffected()
    {
        await using var daemon = new FakeUnixBuildDaemon(
            """{"stream":"Step 1/1 : FROM scratch"}""",
            """{"stream":"Successfully built abc123"}""");
        var client = ScriptedDockerClient.OnSocket(daemon.Endpoint, imageExists: true);

        var built = await BuildOverSocketAsync(client);

        built.Should().Be(Image);
        daemon.ServeFailure.Should().BeNull();
        client.TypedBuildCalls.Should().Be(0);
        client.InspectedImages.Should().Equal(Image);
    }
}
