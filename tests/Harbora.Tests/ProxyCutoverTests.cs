using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The proxy step of the cutover — the one place a deployment used to be able to lie.
///
/// The apply failed, a single "⚠ Proxy apply failed…" line went into the deploy log, and the
/// pipeline carried on to mark the deployment Succeeded. The new container really was running, so
/// nothing looked wrong; traffic was still pointing at the old upstream, or at nothing at all. These
/// tests hold the opposite: routing that did not apply is a failed deployment, said in words, with
/// the previous release left exactly where it was.
/// </summary>
public class ProxyCutoverTests
{
    private const string EngineError = "open /etc/harbora/traefik/dynamic/harbora.yml: permission denied";

    // ---- an apply that fails is a deployment that failed ----

    [Fact]
    public async Task A_proxy_apply_that_fails_fails_the_deployment()
    {
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed,
            "a deployment whose traffic was never switched has not succeeded, whatever the container is doing");
    }

    [Fact]
    public async Task The_recorded_reason_names_the_proxy_and_quotes_the_engines_own_error()
    {
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        // The operator has to be able to act on this without reading the panel's source.
        result.ErrorMessage.Should().Contain("proxy").And.Contain(EngineError);
    }

    [Fact]
    public async Task A_rolled_back_config_file_is_reported_as_rolled_back()
    {
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.ErrorMessage.Should().Contain("rolled back").And.NotContain("not rolled back",
            "the live routes are intact, and saying otherwise would send the operator looking for damage");
    }

    [Fact]
    public async Task A_config_file_that_was_not_rolled_back_says_so()
    {
        // The distinction matters: an unrolled-back file may no longer describe what is running, and
        // that is a thing to go and look at rather than a detail to leave out.
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: false);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.ErrorMessage.Should().Contain("not rolled back");
    }

    [Fact]
    public async Task A_failed_apply_raises_the_same_deploy_failed_alert_as_any_other_failure()
    {
        // Routed through the pipeline's one failure path rather than a second mechanism, so a proxy
        // failure reaches the same alert rules, the same notifications and the same history.
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 1));

        var alert = h.Notifications.Notifications.Should().ContainSingle().Subject;
        alert.Event.Should().Be(AlertEvent.DeployFailed);
        alert.Body.Should().Contain(EngineError);
    }

    // ---- and retires nothing ----

    [Fact]
    public async Task A_failed_apply_leaves_the_previous_release_serving()
    {
        // The cutover order is start new → wire proxy → retire old. A failure at the proxy step is
        // therefore a failure before anything was retired, and that is the whole reason the order is
        // what it is.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Docker.LiveContainerNames.Should().BeEquivalentTo([h.ContainerFor(1)],
            "the release that was serving must still be serving");
        h.Docker.OperationsOn(h.ContainerFor(1)).Should().NotContain("RemoveContainerAsync");
    }

    [Fact]
    public async Task A_failed_apply_removes_only_the_container_it_just_started()
    {
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 2));

        var removed = h.Docker.Calls
            .Where(c => c.Operation == "RemoveContainerAsync").Select(c => c.Target).ToList();
        removed.Should().Equal(h.ContainerFor(2));
    }

    [Fact]
    public async Task A_failed_apply_keeps_the_app_pointing_at_the_release_that_still_works()
    {
        using var h = new PipelineHarness().WithDomain();
        var previous = h.WithPreviousDeployment(number: 1);
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 2));

        var app = await h.Db.Apps.AsNoTracking().FirstAsync(a => a.Id == h.App.Id);
        app.ActiveDeploymentId.Should().Be(previous.Id);
        app.Status.Should().Be(AppStatus.Running, "the previous release is still up and answering");
    }

    // ---- an app with no domains is untouched by any of this ----

    [Fact]
    public async Task An_app_with_no_domains_succeeds_without_the_proxy_being_asked_anything()
    {
        using var h = new PipelineHarness();

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Proxy.ApplyCount.Should().Be(0, "there is nothing to route");
    }

    // ---- optional post-cutover verification, off unless asked for ----

    [Fact]
    public async Task The_proxy_is_never_probed_at_the_default_setting()
    {
        // Off by default is the shipped behaviour, not an oversight: a probe that runs everywhere
        // before there is a live-host lane proving it works would fail deployments that worked.
        using var h = new PipelineHarness().WithDomain();

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Options.VerifyThroughProxy.Should().BeFalse("the default is what most installs run");
        h.Http.Attempts.Should().Be(0, "nothing should have been asked of the proxy");
    }

    [Fact]
    public async Task Verification_that_cannot_connect_fails_the_deployment()
    {
        using var h = new PipelineHarness().WithDomain();
        h.Options.VerifyThroughProxy = true;
        h.Http.Failure = new HttpRequestException("Connection refused (harbora-traefik:80)");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("blog.example.com").And.Contain("Connection refused");
    }

    [Fact]
    public async Task Verification_asks_the_proxy_for_the_apps_primary_domain_exactly_once()
    {
        using var h = new PipelineHarness()
            .WithDomain("alias.example.com")
            .WithDomain("blog.example.com", primary: true);
        h.Options.VerifyThroughProxy = true;

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Http.Attempts.Should().Be(1, "one request is enough to tell a served route from an unserved one");
        // Named in the header, dialled at the proxy: the check must not depend on public DNS or on
        // whatever sits in front of the domain.
        h.Http.RequestedHosts.Should().Equal("blog.example.com");
        h.Http.RequestedUrls.Should().ContainSingle()
            .Which.Should().Contain(h.Options.ProxyContainerName);
    }

    [Fact]
    public async Task Any_answer_from_the_proxy_is_enough_for_verification_to_pass()
    {
        // The proxy redirects plain HTTP to HTTPS, so a healthy install answers 308 here. Judging the
        // status would fail every verified deployment; judging the app's response is the health
        // gate's job and it has already run.
        using var h = new PipelineHarness().WithDomain();
        h.Options.VerifyThroughProxy = true;
        h.Http.Status = System.Net.HttpStatusCode.PermanentRedirect;

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
    }

    [Fact]
    public async Task Verification_is_not_attempted_for_an_app_with_no_domains()
    {
        using var h = new PipelineHarness();
        h.Options.VerifyThroughProxy = true;
        h.Http.Failure = new HttpRequestException("Connection refused");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded, "there is no domain to verify");
        h.Http.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task A_verification_failure_also_leaves_the_previous_release_running()
    {
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Options.VerifyThroughProxy = true;
        h.Http.Failure = new HttpRequestException("Connection refused");

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.OperationsOn(h.ContainerFor(1)).Should().NotContain("RemoveContainerAsync");
    }
}

/// <summary>
/// The wording itself, unit-tested where it is decided. A failure message is the only part of a
/// failed deployment the user actually reads, so what it contains is a rule and not a detail.
/// </summary>
public class ProxyDiagnosisTests
{
    [Fact]
    public void An_apply_failure_quotes_the_engines_error()
    {
        var text = ProxyDiagnosis.ExplainApplyFailure(
            new ProxyApplyResult(false, "permission denied", RolledBack: true));

        text.Should().Contain("permission denied");
    }

    [Fact]
    public void A_rolled_back_file_is_reported_as_intact()
    {
        var text = ProxyDiagnosis.ExplainApplyFailure(
            new ProxyApplyResult(false, "permission denied", RolledBack: true));

        text.Should().Contain("rolled back").And.NotContain("not rolled back");
    }

    [Fact]
    public void A_file_left_as_it_was_is_reported_as_worth_checking()
    {
        var text = ProxyDiagnosis.ExplainApplyFailure(
            new ProxyApplyResult(false, "permission denied", RolledBack: false));

        text.Should().Contain("not rolled back").And.Contain("check it");
    }

    [Fact]
    public void An_engine_that_gave_no_reason_says_that_rather_than_trailing_off()
    {
        var text = ProxyDiagnosis.ExplainApplyFailure(new ProxyApplyResult(false, null, RolledBack: false));

        text.Should().Contain("no reason").And.NotContain("reported: .");
    }

    [Fact]
    public void An_unreachable_domain_names_the_domain_the_probe_and_the_error()
    {
        var text = ProxyDiagnosis.ExplainUnreachable(
            "blog.example.com", "http://harbora-traefik/", "Connection refused");

        text.Should().Contain("blog.example.com")
            .And.Contain("http://harbora-traefik/")
            .And.Contain("Connection refused");
    }

    [Fact]
    public void The_apply_failure_says_the_previous_version_is_still_serving()
    {
        // The cutover order guarantees it, and a user reading "failed" needs to know their site did
        // not go down with the deployment.
        var text = ProxyDiagnosis.ExplainApplyFailure(new ProxyApplyResult(false, "boom", RolledBack: true));

        text.Should().Contain("still serving");
    }
}
