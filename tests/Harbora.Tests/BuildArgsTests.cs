using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P6 (2026-08-17 app-environment-management design): the build-arg path already reaches the
/// pipeline end to end — <c>EnvironmentVariable.AvailableAtBuild</c> →
/// <c>DeploymentPipeline.cs:1194-1199</c> → <c>DockerBuildRequest.BuildArgs</c> → the engine — and had
/// zero test coverage anywhere (0027's own table: "Zero matches for BuildArgs, AvailableAtBuild or
/// X-Build-Args under tests/"). What was actually missing was a checkbox in
/// <c>Views/Apps/Details.cshtml</c>'s add-variable form; the panel could never produce a variable with
/// the flag set, which is why nothing here was ever exercised.
///
/// <para>
/// Proved against <see cref="Fakes.FakeDockerEngine.BuildRequests"/> — what the pipeline actually
/// handed the engine — rather than against the checkbox rendering, per the brief: a checkbox that
/// renders correctly but is wired to nothing would pass a view-only test and still ship the same gap.
/// </para>
/// </summary>
public class BuildArgsTests
{
    [Fact]
    public async Task A_variable_marked_available_at_build_reaches_the_docker_build_request()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();

        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "BUILD_FLAG", Value = "yes", IsSecret = false, AvailableAtBuild = true
        });
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "RUNTIME_ONLY", Value = "no", IsSecret = false, AvailableAtBuild = false
        });
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment();
        await h.RunAsync(deployment);

        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.BuildArgs.Should().ContainKey("BUILD_FLAG").WhoseValue.Should().Be("yes");
        build.BuildArgs.Should().NotContainKey("RUNTIME_ONLY",
            "a variable not marked available at build must never reach the build layer");
    }

    /// <summary>A build-time secret is decrypted before it reaches the build, the same way a runtime
    /// secret is decrypted before it reaches the container — the flag changes when it is revealed,
    /// not whether it stays encrypted forever.</summary>
    [Fact]
    public async Task A_secret_marked_available_at_build_reaches_the_build_decrypted()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository)
            .WithGitSource().WithDockerfile();

        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "BUILD_SECRET",
            Value = h.Protector.Protect("s3cr3t"), IsSecret = true, AvailableAtBuild = true
        });
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment();
        await h.RunAsync(deployment);

        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.BuildArgs.Should().ContainKey("BUILD_SECRET").WhoseValue.Should().Be("s3cr3t");
    }

    /// <summary>P6: compose-service builds get their own build args too — before this, every service
    /// in a stack built with an empty dictionary regardless of what the app declared
    /// (<c>DeploymentPipeline.cs:924-925</c>).</summary>
    [Fact]
    public async Task A_compose_services_build_also_receives_the_apps_build_time_variables()
    {
        using var h = new Fakes.PipelineHarness()
            .WithComposeFile("""
                services:
                  web:
                    build: .
                    ports:
                      - "8080:8080"
                """)
            .WithDockerfile();

        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "BUILD_FLAG", Value = "yes", IsSecret = false, AvailableAtBuild = true
        });
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment();
        await h.RunAsync(deployment);

        h.Docker.BuildRequests.Should().ContainSingle(r => r.BuildArgs.ContainsKey("BUILD_FLAG"));
    }
}
