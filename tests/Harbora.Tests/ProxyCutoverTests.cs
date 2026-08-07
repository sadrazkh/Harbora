using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
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

    // ---- and leaves the stored routes describing what is actually running ----

    [Fact]
    public async Task A_failed_apply_leaves_the_stored_route_naming_the_container_that_is_still_serving()
    {
        // The rows are written before the apply, because the config is rendered from the platform's
        // stored routes and a first deployment's own route has to be in it. That means a failed
        // apply can leave them naming the container the failure path is about to remove — and every
        // other caller (RoutesController, AppsController, AdminerService, AppOperationsService)
        // re-applies from those same rows. The next unrelated route change anywhere would then publish
        // a dead upstream and take down a domain the rolled-back config was still serving correctly.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = h.Workspace.Id, AppId = h.App.Id, Host = "blog.example.com",
            TargetService = h.ContainerFor(1), TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 2));

        var route = await h.Db.Routes.AsNoTracking().SingleAsync(r => r.Host == "blog.example.com");
        route.TargetService.Should().Be(h.ContainerFor(1),
            "the stored route must keep describing the release that is still up, or the next re-apply " +
            "from anywhere on this platform would publish an upstream that no longer exists");
        route.TargetPort.Should().Be(8080);
    }

    [Fact]
    public async Task A_first_deployment_that_fails_to_apply_leaves_no_route_row_behind()
    {
        // Nothing was routing this domain before, so the honest revert is no row at all — a disabled
        // or dangling one would still be a row an operator has to explain.
        using var h = new PipelineHarness().WithDomain();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Db.Routes.AsNoTracking().Should().BeEmpty("this deployment created the row and this deployment failed");
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
        h.Http.Attempts.Should().Be(1, "one request is all this check is: does the proxy answer");
        // Named in the header, dialled at the proxy: the check must not depend on public DNS or on
        // whatever sits in front of the domain. The header does not make the answer domain-specific
        // — see Any_answer_from_the_proxy_is_enough_for_verification_to_pass — but it is what says
        // which domain the deployment was for when this fails.
        h.Http.RequestedHosts.Should().Equal("blog.example.com");
        // No port in what comes back: the request is built with one, and Uri drops :80 from an http
        // URL because it is the scheme's default. A port that is NOT the default survives — see
        // The_probe_dials_the_port_the_proxy_was_configured_to_answer_on, which is where that is held.
        h.Http.RequestedUrls.Should().ContainSingle()
            .Which.Should().Be($"http://{h.Options.ProxyContainerName}/");
    }

    [Fact]
    public async Task The_probe_dials_the_port_the_proxy_was_configured_to_answer_on()
    {
        // The container name was a setting and the port was a literal 80, so an install that moved
        // its proxy's plain-HTTP entry point had this check dial a closed port — and, with the flag
        // on, fail every deployment of every app that has a domain. A knob with no companion is how
        // a configurable thing stays half-configurable.
        using var h = new PipelineHarness().WithDomain();
        h.Options.VerifyThroughProxy = true;
        h.Options.ProxyHttpPort = 8081;

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Http.RequestedUrls.Should().ContainSingle().Which.Should().Contain(":8081");
    }

    [Fact]
    public void The_probe_port_defaults_to_the_one_the_shipped_proxy_listens_on()
    {
        // deploy/docker-compose.yml gives Traefik its `web` entrypoint on 80, and the default has to
        // keep matching it: this option exists to let an install differ, not to make it declare.
        new HarboraRuntimeOptions().ProxyHttpPort.Should().Be(80);
    }

    [Fact]
    public async Task Any_answer_from_the_proxy_is_enough_for_verification_to_pass()
    {
        // The redirect to HTTPS is configured on the ENTRYPOINT, not on a router, so Traefik answers
        // 308 to everything arriving on :80 before it looks at any route. A healthy install therefore
        // always answers 308 here, and judging the status would fail every verified deployment.
        // It is also why this check proves reachability and not routing — see VerifyThroughProxy.
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
    public async Task A_verification_failure_takes_the_new_container_back_out_of_the_live_config()
    {
        // On this path the apply already SUCCEEDED, so the live config named the new container —
        // which the pipeline's failure path then removes. Leaving it there made the domain dark
        // until something, anything, re-applied; it was left there because a second apply could be
        // refused outright by an unrelated tenant's invalid row and was not worth trusting. It is
        // now, so the routing goes back to what this deployment found: no row at all here, because
        // nothing was routing this host before it.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Options.VerifyThroughProxy = true;
        h.Http.Failure = new HttpRequestException("Connection refused");

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Failed);
        h.Docker.OperationsOn(h.ContainerFor(1)).Should().NotContain("RemoveContainerAsync",
            "the container is untouched — the cutover retires nothing until after this step");
        h.Proxy.ApplyCount.Should().Be(2, "the routing this deployment published is published back");
        h.Proxy.Live.Should().BeEmpty(
            "nothing may still name the container this failure is about to remove");
    }

    [Fact]
    public async Task A_failed_apply_publishes_the_routing_it_put_back()
    {
        // The gap the rows alone cannot close. They are saved before the apply — they have to be,
        // the config is rendered from them — so between the save and the failure, any other caller's
        // apply publishes this deployment's new upstream on its behalf. Reverting the rows makes the
        // NEXT apply correct; it does not make the live config correct, and nobody is obliged to
        // apply again. So this deployment does, with what it put back.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = h.Workspace.Id, AppId = h.App.Id, Host = "blog.example.com",
            TargetService = h.ContainerFor(1), TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: false);

        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Proxy.Live.Should().ContainSingle()
            .Which.TargetService.Should().Be(h.ContainerFor(1),
                "the release that is still running is the one the live config must name");
    }

    [Fact]
    public async Task The_revert_restores_what_was_there_when_two_domains_carry_one_host()
    {
        // Two domains on one host resolve to one Route row, so the second pass through the loop gets
        // back the instance the first has already rewritten. Capturing it again would record this
        // deployment's own new upstream as the thing to restore, and the revert would then "restore"
        // the container it is about to remove — the exact failure it exists to prevent, arriving
        // through its own bookkeeping.
        //
        // Domains.Host is unique in the schema, so this pair cannot be created through the panel
        // today; the guard is here anyway, because what the revert restores is now published to the
        // live config immediately, and that is not a thing to leave resting on an index in another
        // table.
        using var h = new PipelineHarness().WithDomain().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = h.Workspace.Id, AppId = h.App.Id, Host = "blog.example.com",
            TargetService = h.ContainerFor(1), TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.Proxy.Result = new ProxyApplyResult(false, EngineError, RolledBack: true);

        await h.RunAsync(h.QueueDeployment(number: 2));

        var route = await h.Db.Routes.AsNoTracking().SingleAsync(r => r.Host == "blog.example.com");
        route.TargetService.Should().Be(h.ContainerFor(1),
            "the row said this before the deployment touched it, however many domains pointed at it");
    }

    [Fact]
    public async Task A_verification_failure_still_leaves_the_stored_route_naming_the_running_container()
    {
        // The live config cannot be restored from here without a second apply, but the stored rows
        // can — and must, for the same reason as a failed apply: they are what every other caller
        // re-applies from. Reverted, the next route change anywhere on the platform heals the domain
        // instead of nailing the dead upstream in place.
        using var h = new PipelineHarness().WithDomain();
        h.WithPreviousDeployment(number: 1);
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = h.Workspace.Id, AppId = h.App.Id, Host = "blog.example.com",
            TargetService = h.ContainerFor(1), TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.Options.VerifyThroughProxy = true;
        h.Http.Failure = new HttpRequestException("Connection refused");

        await h.RunAsync(h.QueueDeployment(number: 2));

        var route = await h.Db.Routes.AsNoTracking().SingleAsync(r => r.Host == "blog.example.com");
        route.TargetService.Should().Be(h.ContainerFor(1));
    }

    [Fact]
    public async Task A_domain_that_cannot_be_put_in_a_request_is_refused_in_words()
    {
        // Setting the Host header on a malformed domain throws FormatException, which used to escape
        // to the pipeline's catch and fail the deployment with "The format of value 'not a host' is
        // invalid" — no domain, no app, no proxy, nothing to act on, from the one method whose entire
        // job is to say what went wrong.
        using var h = new PipelineHarness().WithDomain("not a host");
        h.Options.VerifyThroughProxy = true;

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("not a host").And.Contain("host name");
        h.Http.Attempts.Should().Be(0, "there was never a request that could have been made");
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
    public void An_unreachable_proxy_is_not_reported_as_a_route_that_did_not_match()
    {
        // The check cannot tell those apart — it reaches the entrypoint, which redirects before any
        // router is consulted — so the message must claim only what failed: the proxy did not answer.
        var text = ProxyDiagnosis.ExplainUnreachable(
            "blog.example.com", "http://harbora-traefik/", "Connection refused");

        text.Should().Contain("the proxy itself did not answer")
            .And.NotContain("route", "nothing here established anything about the route");
    }

    [Fact]
    public void An_unusable_domain_names_the_domain_and_what_to_do_about_it()
    {
        var text = ProxyDiagnosis.ExplainUnusableHost(
            "not a host", "The specified value is not a valid 'Host' header string.");

        text.Should().Contain("not a host").And.Contain("host name").And.Contain("Correct the domain");
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
