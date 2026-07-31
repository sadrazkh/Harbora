using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The command that runs before a release goes live — database migrations, most often.
///
/// The guarantee that makes it worth having: <b>if it fails, the version that is already serving keeps
/// serving</b>. That is precisely why it does not live inside the container's own start-up, where a
/// failed migration takes the site down with it.
/// </summary>
public class ReleaseTaskTests
{
    [Fact]
    public async Task A_release_task_runs_from_the_new_image_before_the_container_starts()
    {
        // Order is the whole point: migrate first, then start, then switch.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "dotnet ef database update";
        h.Db.SaveChanges();
        var deployment = h.QueueDeployment(number: 1);

        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.OneOffCommands.Should().ContainSingle()
            .Which.Should().Contain("dotnet ef database update");

        var task = h.Docker.IndexOf("RunOneOffAsync", h.Docker.RunRequests[0].Image);
        var started = h.Docker.IndexOf("RunContainerAsync", h.ContainerFor(1));
        task.Should().BeGreaterThanOrEqualTo(0);
        started.Should().BeGreaterThan(task, "the migration must finish before the new container starts");
    }

    [Fact]
    public async Task A_failed_release_task_leaves_the_previous_version_serving()
    {
        // The reason this feature exists. A failed migration must not become an outage.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.App.ReleaseCommand = "dotnet ef database update";
        h.Db.SaveChanges();
        h.Docker.OneOffExitCode = 1;

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("release task").And.Contain("still serving");
        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)], "the old container is untouched");
        h.Proxy.ApplyCount.Should().Be(0, "traffic never moved");
    }

    [Fact]
    public async Task A_failed_release_task_never_starts_the_new_container()
    {
        // Cheaper and safer than starting it and tearing it down: nothing from the new version ever
        // runs against a database the migration failed to prepare.
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.App.ReleaseCommand = "migrate";
        h.Db.SaveChanges();
        h.Docker.OneOffExitCode = 2;

        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Docker.OperationsOn(h.ContainerFor(2)).Should().NotContain("RunContainerAsync");
    }

    [Fact]
    public async Task A_service_without_a_release_command_runs_nothing_extra()
    {
        // The overwhelmingly common case must be untouched — and must not pay for a container.
        using var h = new PipelineHarness();

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.OneOffCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_release_command_is_treated_as_none()
    {
        // A field someone cleared should not run an empty shell.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "   ";
        h.Db.SaveChanges();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.OneOffCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_task_that_never_finishes_is_given_up_on_rather_than_waited_on_for_ever()
    {
        // Observed live: a command that never exits left the deployment "in progress" indefinitely,
        // with nothing on the screen to click and no way to tell a slow migration from a stuck one.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.App.ReleaseCommand = "dotnet ef database update";
        h.Db.SaveChanges();
        h.Docker.OneOffNeverFinishes = true;
        h.Options.ReleaseTaskTimeoutMinutes = 0.001;

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("still serving");
        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)], "the old container is untouched");
    }

    [Fact]
    public async Task An_image_with_no_shell_says_so_instead_of_repeating_dockers_complaint_about_sh()
    {
        // Scratch and distroless images have no /bin/sh. Docker's own wording blames "sh", which
        // reads like a typo in the command rather than a property of the base image.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "migrate";
        h.Db.SaveChanges();
        h.Docker.OneOffThrows = new InvalidOperationException(
            "OCI runtime create failed: exec: \"sh\": executable file not found in $PATH");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("no shell");
    }

    [Fact]
    public async Task Output_carrying_dockers_framing_bytes_does_not_break_the_deployment()
    {
        // Exactly how this failed in production. Docker frames the output of a container with no TTY,
        // NUL bytes and all; PostgreSQL rejects a NUL in a text column outright; and because the
        // rejection lands inside SaveChanges, the pipeline could not even record its own failure.
        // The deployment sat "in progress" indefinitely.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "dotnet ef database update";
        h.Db.SaveChanges();
        h.Docker.OneOffOutput.Add("\0\0\0\0\0\0MIGRATION_DONE");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var logs = h.Db.DeploymentLogs.Where(l => l.DeploymentId == result.Id).Select(l => l.Message).ToList();
        logs.Should().NotContain(m => m.Contains('\0'), "a NUL byte cannot be stored at all");
        logs.Should().Contain(m => m.Contains("MIGRATION_DONE"), "and the output itself must survive");
    }

    [Fact]
    public async Task A_failure_whose_output_carries_framing_bytes_is_still_recorded()
    {
        // The nastier half of the same bug, and the one that survived the first fix: a failure
        // message quotes the failing command's output, so it carries the same unstorable bytes. The
        // write that records the failure then throws, and the deployment cannot even report that it
        // failed — it stays "in progress" while the release task has plainly finished.
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.App.ReleaseCommand = "dotnet ef database update";
        h.Db.SaveChanges();
        h.Docker.OneOffExitCode = 3;
        h.Docker.OneOffOutput.Add("\0\0\0\0\0\0COLUMN_ALREADY_EXISTS");

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().NotBeNull().And.NotContain("\0");
        result.ErrorMessage.Should().Contain("COLUMN_ALREADY_EXISTS", "the reason must survive the cleaning");
    }

    [Fact]
    public async Task A_stored_failure_message_has_secrets_removed()
    {
        // The stored copy is shown on the deployment page. It used to be saved raw while only the
        // log line was redacted, so a command that echoed a secret kept it in the database.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "migrate";
        h.App.EnvironmentVariables.Add(new Harbora.Domain.Apps.EnvironmentVariable
        {
            Key = "DB_PASSWORD", Value = h.Protector.Protect("hunter2"), IsSecret = true
        });
        h.Db.SaveChanges();
        h.Docker.OneOffExitCode = 1;
        h.Docker.OneOffOutput.Add("FATAL: password authentication failed for user hunter2");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task The_release_task_gets_the_apps_environment()
    {
        // A migration without the connection string is a migration that fails for no reason anyone
        // could guess from the message.
        using var h = new PipelineHarness();
        h.App.ReleaseCommand = "migrate";
        h.App.EnvironmentVariables.Add(new Harbora.Domain.Apps.EnvironmentVariable
        {
            Key = "ConnectionStrings__Default", Value = "Host=db;Database=shop", IsSecret = false
        });
        h.Db.SaveChanges();

        await h.RunAsync(h.QueueDeployment(number: 1));

        var request = h.Docker.OneOffRequests.Should().ContainSingle().Subject;
        request.Env.Should().ContainKey("ConnectionStrings__Default");
        request.NetworkMode.Should().NotBeNullOrEmpty("it has to reach the database it is migrating");
    }
}
