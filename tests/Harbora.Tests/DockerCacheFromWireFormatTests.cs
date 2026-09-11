using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The exact bytes <c>cachefrom</c> goes out as.
///
/// <para>
/// The build-cache feature (1.1, 2026-09-02) shipped with <c>ImageBuildParameters.CacheFrom</c> set
/// to a plain list. That property is annotated with Docker.DotNet's
/// <c>EnumerableQueryStringConverter</c>, which writes one <c>cachefrom=</c> pair per element with
/// each value escaped but otherwise verbatim. The daemon expects a JSON array, tries to parse the
/// first bare value, and answers <c>400 error reading cache-from: invalid character …</c> — while
/// the client is still writing a multi-megabyte context, so the write dies with <c>Broken pipe</c>
/// and the 400 is never read. Deployments 31-39 on the live server all failed exactly this way,
/// identically, whatever the app's code was, with no build step ever reported.
/// </para>
///
/// <para>
/// Both forms were checked against the real daemon on that server, with a 6 MB context: the bare
/// form answers <b>400</b>, the JSON-array form answers <b>200</b> and streams <c>Step</c> lines.
/// These tests pin the shape so the feature cannot regress into the form that cannot work — the
/// daemon is not available here to catch it, and nothing else in this suite looks at the wire.
/// </para>
/// </summary>
public class DockerCacheFromWireFormatTests
{
    [Fact]
    public void The_typed_call_sends_one_element_that_is_the_whole_json_array()
    {
        var encoded = DockerEngine.JsonEncodedCacheFrom(["img:a", "img:b"]);

        // One element, not two: EnumerableQueryStringConverter writes a pair per element, so a
        // two-element list is two bare cachefrom= pairs and the daemon rejects the first.
        encoded.Should().ContainSingle();
        encoded![0].Should().Be("""["img:a","img:b"]""");
    }

    [Fact]
    public void That_single_element_parses_back_as_the_images_it_names()
    {
        var encoded = DockerEngine.JsonEncodedCacheFrom(["harbora/driveunion:build-30"]);

        JsonSerializer.Deserialize<string[]>(encoded![0])
            .Should().Equal("harbora/driveunion:build-30");
    }

    [Fact]
    public void Nothing_to_reuse_sends_no_cachefrom_at_all()
    {
        // An empty array would still be a cachefrom= the daemon has to parse. A cold build says
        // nothing rather than saying "nothing".
        DockerEngine.JsonEncodedCacheFrom(null).Should().BeNull();
        DockerEngine.JsonEncodedCacheFrom([]).Should().BeNull();
    }

    [Fact]
    public void BuildParameters_keeps_the_plain_list_for_the_socket_transport()
    {
        // DockerBuildTransport serializes the list itself. If BuildParameters pre-encoded it too,
        // that path would send a JSON array nested inside a JSON array — the same 400, from the
        // opposite mistake.
        var parameters = DockerEngine.BuildParameters(
            "Dockerfile", "app:1", new Dictionary<string, string>(), ["img:a", "img:b"], noCache: false);

        parameters.CacheFrom.Should().Equal("img:a", "img:b");
    }

    [Fact]
    public void The_socket_transport_handles_the_unix_endpoints_and_leaves_the_others_alone()
    {
        // Which branch a build takes decides which of the two encodings is correct for it, so the
        // predicate that chooses is part of this contract.
        DockerBuildTransport.Handles(new Uri("unix:///var/run/docker.sock")).Should().BeTrue();
        DockerBuildTransport.Handles(new Uri("unix+http://localhost")).Should().BeTrue();
        DockerBuildTransport.Handles(new Uri("npipe://./pipe/docker_engine")).Should().BeFalse();
        DockerBuildTransport.Handles(new Uri("tcp://10.0.0.5:2375")).Should().BeFalse();
        DockerBuildTransport.Handles(null).Should().BeFalse();
    }
}
