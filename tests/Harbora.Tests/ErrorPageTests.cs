using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Harbora.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Error pages. The original defect was blunt: <c>HomeController.Error()</c> rendered a view that did
/// not exist, and nothing handled 404 at all — so a mistyped URL produced a blank page with a bare
/// status code, and a real exception produced a second exception.
/// </summary>
public class ErrorPageTests
{
    private static HomeController NewController()
    {
        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("err-" + Guid.NewGuid()).Options);

        return new HomeController(db, new FakeDockerEngine(),
            new Harbora.Infrastructure.Dashboard.AttentionService(db, new FixedClock()),
            new Harbora.Infrastructure.Monitoring.NetworkHistory(db),
            new Harbora.Tests.Fakes.FakeManagedServiceEngine(),
            new AnonymousUser(), NullLogger<HomeController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? Email => null;
        public bool IsAuthenticated => false;
        public Guid? WorkspaceId => null;
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public void A_status_page_keeps_the_real_status_code(int code)
    {
        // Re-executing into a themed page must not turn a 404 into a 200 — crawlers, monitoring and
        // the browser's own history all depend on the real code.
        var controller = NewController();

        controller.HttpStatus(code);

        controller.Response.StatusCode.Should().Be(code);
    }

    [Fact]
    public void A_status_page_renders_the_shared_error_view()
    {
        var controller = NewController();

        var result = controller.HttpStatus(404).Should().BeOfType<ViewResult>().Subject;

        result.ViewName.Should().Be("Error");
        result.Model.Should().BeOfType<ErrorViewModel>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void The_exception_page_reports_500()
    {
        var controller = NewController();

        var result = controller.Error().Should().BeOfType<ViewResult>().Subject;

        controller.Response.StatusCode.Should().Be(500);
        result.ViewName.Should().Be("Error");
        result.Model.Should().BeOfType<ErrorViewModel>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public void Every_error_page_carries_a_reference_id()
    {
        // Without this the only thing a user can report is "it broke".
        var controller = NewController();

        var model = (ErrorViewModel)((ViewResult)controller.HttpStatus(500)).Model!;

        model.ShowRequestId.Should().BeTrue();
        model.RequestId.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The exact regression: a view referenced by an action but absent from disk fails only at
    /// runtime, and the failure surfaces *while already handling an error* — so the user sees
    /// nothing useful and the real cause is buried.
    /// </summary>
    [Fact]
    public void The_error_view_exists_on_disk()
    {
        var repoRoot = FindRepoRoot();
        var view = Path.Combine(repoRoot, "src", "Harbora.Web", "Views", "Home", "Error.cshtml");

        File.Exists(view).Should().BeTrue($"HomeController renders \"Error\"; expected the view at {view}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
