using System.Reflection;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Assistant;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Security;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Harbora.Web.Controllers.Api;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The two halves of a deployment somebody is watching but cannot act on: why it is queued, and how
/// to stop it.
///
/// <para>
/// <c>DeploymentEngine.CancelAsync</c> and <c>Job.CancelRequested</c> both existed and were reachable
/// from nowhere — no button, no endpoint, no command — so a deploy queued behind a long build could
/// only be waited out. The cases that matter most here are the honest ones: a deployment that reached
/// a terminal state between the page being drawn and the button being pressed has to answer clearly
/// rather than throw an illegal-transition exception or, worse, report a cancellation that did not
/// happen.
/// </para>
/// </summary>
public class DeploymentQueueAndCancelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    // ---- doubles ----

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public readonly List<string> Actions = [];

        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what the engine asked of the queue, without a worker in the room.</summary>
    private sealed class RecordingQueue : IJobQueue
    {
        public readonly List<(JobKind Kind, Guid TargetId)> Cancelled = [];

        public Task<Guid> EnqueueAsync(
            JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
            => Task.FromResult(Guid.CreateVersion7());

        public Task<Guid> EnqueueExclusiveAsync(
            JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null,
            CancellationToken ct = default)
            => Task.FromResult(Guid.CreateVersion7());

        public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default)
        {
            Cancelled.Add((kind, targetId));
            return Task.FromResult(true);
        }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    // ---- fixture ----

    private sealed record Fixture(
        HarboraDbContext Db,
        DeploymentsController Panel,
        ApiV1Controller Api,
        RecordingQueue Queue,
        Deployment Deployment,
        Guid AppId,
        Guid WorkspaceId);

    private static Fixture Build(
        DeploymentStatus status = DeploymentStatus.Queued,
        SystemRole role = SystemRole.Owner,
        int maxConcurrency = 4)
    {
        var workspaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("deploy-cancel-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(workspaceId));

        var app = new App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ServerId = Guid.CreateVersion7(),
            Name = "Shop", Slug = "shop"
        };
        db.Apps.Add(app);

        db.Users.Add(new User
        {
            Id = userId, Email = "me@example.com", DisplayName = "Tester",
            Role = role, ScopedToProjects = false
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role switch
            {
                SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
                SystemRole.Operator => WorkspaceRole.Operator,
                SystemRole.Viewer => WorkspaceRole.Viewer,
                _ => WorkspaceRole.Member
            }
        });

        var deployment = new Deployment
        {
            Id = Guid.CreateVersion7(), AppId = app.Id, WorkspaceId = workspaceId,
            Number = 42, Status = status, Trigger = DeploymentTrigger.Manual, CreatedAt = Now
        };
        db.Deployments.Add(deployment);
        db.SaveChanges();

        var caller = new Caller(workspaceId, userId);
        var queue = new RecordingQueue();
        var engine = new DeploymentEngine(db, queue, new FixedClock(Now));
        var access = new ProjectAccessService(db, caller);
        var options = Options.Create(new Harbora.Infrastructure.Jobs.JobQueueOptions
        {
            MaxConcurrency = maxConcurrency
        });

        var assistant = new AssistantService(
            db, new AssistantClient(new StubHttpClientFactory()), new PassthroughProtector(),
            NullLogger<AssistantService>.Instance);

        var panel = new DeploymentsController(
            db, access, assistant, engine, new SilentAudit(), caller, options, new FixedClock(Now))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        var api = new ApiV1Controller(
            db, engine, Options.Create(new HarboraRuntimeOptions()),
            passwordHasher: null!, tokens: null!, new SilentAudit(), caller)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Fixture(db, panel, api, queue, deployment, app.Id, workspaceId);
    }

    /// <summary>Adds a Jobs row of the shape the queue actually writes for this kind.</summary>
    private static void Enqueue(
        HarboraDbContext db, JobKind kind, Guid targetId, Guid? exclusiveWith = null,
        JobStatus jobStatus = JobStatus.Pending, int minutesOld = 0)
    {
        db.Jobs.Add(new Job
        {
            Id = Guid.CreateVersion7(), Kind = kind, TargetId = targetId, ExclusiveWith = exclusiveWith,
            Status = jobStatus, CreatedAt = Now.AddMinutes(-minutesOld)
        });
        db.SaveChanges();
    }

    // ---- the API endpoint ----

    [Fact]
    public async Task Cancelling_a_queued_deployment_settles_the_row_and_stops_the_job()
    {
        var f = Build();

        var result = await f.Api.CancelDeployment(f.Deployment.Id, default);

        result.Should().BeOfType<OkObjectResult>();
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Cancelled);

        // The record and the work. Settling only the row would leave the queued job to be claimed
        // and to start building a deployment that is already marked cancelled.
        f.Queue.Cancelled.Should().ContainSingle()
            .Which.Should().Be((JobKind.Deployment, f.Deployment.Id));
    }

    [Fact]
    public async Task Cancelling_a_deployment_that_is_already_building_is_accepted()
    {
        var f = Build(DeploymentStatus.Building);

        var result = await f.Api.CancelDeployment(f.Deployment.Id, default);

        result.Should().BeOfType<OkObjectResult>();
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Cancelled);
    }

    [Fact]
    public async Task A_deployment_that_already_finished_is_answered_clearly_and_not_thrown_at()
    {
        // The render-then-click race. The state machine would throw on Succeeded → Cancelled, and an
        // exception here would read as a broken panel rather than as "it finished before you asked".
        var f = Build(DeploymentStatus.Succeeded);

        var result = await f.Api.CancelDeployment(f.Deployment.Id, default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("Succeeded");
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Succeeded);
        f.Queue.Cancelled.Should().BeEmpty("nothing was running to interrupt");
    }

    [Fact]
    public async Task An_unknown_deployment_is_not_found()
    {
        var f = Build();

        (await f.Api.CancelDeployment(Guid.CreateVersion7(), default))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Another_workspaces_deployment_is_not_found_either()
    {
        // Same answer as "no such deployment", deliberately: distinguishing them would confirm that
        // an id belongs to somebody.
        var f = Build();
        var other = new Deployment
        {
            Id = Guid.CreateVersion7(), AppId = Guid.CreateVersion7(), WorkspaceId = Guid.CreateVersion7(),
            Number = 1, Status = DeploymentStatus.Queued, CreatedAt = Now
        };
        f.Db.Deployments.Add(other);
        await f.Db.SaveChangesAsync();

        (await f.Api.CancelDeployment(other.Id, default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void The_cancel_endpoint_asks_for_the_same_capability_as_deploying()
    {
        var authorize = typeof(ApiV1Controller)
            .GetMethod(nameof(ApiV1Controller.CancelDeployment))!
            .GetCustomAttributes<AuthorizeAttribute>().ToList();

        authorize.Should().ContainSingle();
        authorize[0].Policy.Should().Be(Capabilities.AppsDeploy);
        authorize[0].AuthenticationSchemes.Should().Be(TokenAuthenticationHandler.SchemeName);
    }

    // ---- the panel ----

    [Fact]
    public async Task A_queued_deployments_page_says_where_it_is_in_the_queue()
    {
        // One slot, so the backup in front genuinely is in the way — the configured concurrency has
        // to reach the sentence, or the page would promise a wait the queue is not going to make.
        var f = Build(maxConcurrency: 1);
        Enqueue(f.Db, JobKind.Backup, Guid.CreateVersion7(), minutesOld: 10);
        Enqueue(f.Db, JobKind.Deployment, f.Deployment.Id, exclusiveWith: f.AppId, minutesOld: 1);

        var view = (await f.Panel.Details(f.Deployment.Id, default)).Should().BeOfType<ViewResult>().Subject;

        var place = view.ViewData["QueuePlace"].Should().BeOfType<QueuePlace>().Subject;
        place.Position.Should().Be(2);
        place.Ahead.Should().Equal(JobKind.Backup);
        view.ViewData["QueueExplanation"].Should().BeOfType<string>()
            .Which.Should().Contain("backup");
    }

    [Fact]
    public async Task A_deployment_queued_behind_another_of_its_own_app_is_told_that_instead()
    {
        var f = Build();
        Enqueue(f.Db, JobKind.Deployment, Guid.CreateVersion7(), exclusiveWith: f.AppId,
            jobStatus: JobStatus.Running, minutesOld: 5);
        Enqueue(f.Db, JobKind.Deployment, f.Deployment.Id, exclusiveWith: f.AppId, minutesOld: 1);

        var view = (await f.Panel.Details(f.Deployment.Id, default)).Should().BeOfType<ViewResult>().Subject;

        view.ViewData["QueuePlace"].Should().BeOfType<QueuePlace>()
            .Which.Wait.Should().Be(QueueWait.BlockedBySameTarget);
        view.ViewData["QueueExplanation"].Should().BeOfType<string>()
            .Which.Should().Contain("app");
    }

    [Fact]
    public async Task The_queue_position_is_read_from_the_row_the_cancel_endpoint_would_act_on()
    {
        // QueuePlaceAsync used to take the newest job row for the deployment regardless of status;
        // DatabaseJobQueue.RequestCancellationAsync has only ever taken the newest Pending-or-Running
        // one. A stray settled row newer than the live one used to make the two disagree about which
        // job the page and the cancel button were even talking about — here, hiding the real queue
        // position behind "this deployment has no queue at all".
        var f = Build(maxConcurrency: 1);
        Enqueue(f.Db, JobKind.Deployment, f.Deployment.Id, exclusiveWith: f.AppId,
            jobStatus: JobStatus.Pending, minutesOld: 5);
        Enqueue(f.Db, JobKind.Deployment, f.Deployment.Id, exclusiveWith: f.AppId,
            jobStatus: JobStatus.Cancelled, minutesOld: 1);

        var view = (await f.Panel.Details(f.Deployment.Id, default)).Should().BeOfType<ViewResult>().Subject;

        view.ViewData["QueuePlace"].Should().BeOfType<QueuePlace>()
            .Which.Wait.Should().Be(QueueWait.Next,
                "the live Pending row is still queued even though a newer, settled row also exists");
    }

    [Fact]
    public async Task A_finished_deployments_page_explains_no_queue_at_all()
    {
        var f = Build(DeploymentStatus.Succeeded);

        var view = (await f.Panel.Details(f.Deployment.Id, default)).Should().BeOfType<ViewResult>().Subject;

        view.ViewData["QueuePlace"].Should().BeNull();
        view.ViewData["QueueExplanation"].Should().BeNull();
    }

    [Fact]
    public async Task The_page_offers_cancel_only_to_somebody_who_could_deploy()
    {
        var allowed = Build(role: SystemRole.Owner);
        var view = (await allowed.Panel.Details(allowed.Deployment.Id, default))
            .Should().BeOfType<ViewResult>().Subject;
        view.ViewData["CanCancel"].Should().Be(true);

        var denied = Build(role: SystemRole.Viewer);
        var viewerView = (await denied.Panel.Details(denied.Deployment.Id, default))
            .Should().BeOfType<ViewResult>().Subject;
        viewerView.ViewData["CanCancel"].Should().Be(false);
    }

    [Fact]
    public async Task Cancelling_from_the_panel_settles_the_deployment_and_says_so()
    {
        var f = Build();

        var result = await f.Panel.Cancel(f.Deployment.Id, default);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(DeploymentsController.Details));
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Cancelled);
        f.Panel.TempData["Message"].Should().BeOfType<string>().Which.Should().Contain("42");
        f.Panel.TempData.Should().NotContainKey("Error");
    }

    [Fact]
    public async Task A_deployment_that_ended_between_the_page_and_the_button_says_so()
    {
        var f = Build(DeploymentStatus.Failed);

        var result = await f.Panel.Cancel(f.Deployment.Id, default);

        result.Should().BeOfType<RedirectToActionResult>();
        f.Panel.TempData["Error"].Should().BeOfType<string>().Which.Should().Contain("Failed");
        f.Panel.TempData.Should().NotContainKey("Message");
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Failed);
    }

    [Fact]
    public async Task Somebody_who_cannot_deploy_cannot_cancel_either()
    {
        var f = Build(role: SystemRole.Viewer);

        var result = await f.Panel.Cancel(f.Deployment.Id, default);

        result.Should().BeOfType<ForbidResult>();
        (await f.Db.Deployments.FindAsync(f.Deployment.Id))!.Status.Should().Be(DeploymentStatus.Queued);
    }

    [Fact]
    public void The_panels_cancel_is_a_guarded_post()
    {
        var method = typeof(DeploymentsController).GetMethod(nameof(DeploymentsController.Cancel))!;

        method.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should()
            .NotBeNull("a cancel reachable by a cross-site GET would stop deployments from a link");
        method.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(Capabilities.AppsDeploy);
    }

    // ---- the view that renders it ----

    [Fact]
    public void The_deployment_page_renders_the_explanation_and_the_cancel_form()
    {
        var view = File.ReadAllText(
            Path.Combine(TestPaths.WebRoot, "Views", "Deployments", "Details.cshtml"));

        view.Should().Contain("QueueExplanation");
        view.Should().Contain("asp-action=\"Cancel\"");

        // Cheap for a queued deploy, weighty mid-flight — the repo's two confirmation tiers, chosen
        // by status rather than applied to both or to neither.
        view.Should().Contain("confirm(");
    }
}
