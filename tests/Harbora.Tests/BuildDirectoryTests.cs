using System.Diagnostics;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Maintenance;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// Harbora.Domain.Common has a LogLevel of its own (build output); this file means the logging one.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Harbora.Tests;

/// <summary>
/// A build's working directory is removed once nothing needs it, and what older versions left behind
/// is swept without touching anything a deployment still owns.
///
/// <para>
/// Every build used to stay on the <c>harbora_builds</c> volume for ever: the only deletes ran before
/// a checkout, into a path named for a deployment number that is never reused. These tests use real
/// directories in a temporary work root, because what is under test is what is on the disk after.
/// </para>
/// </summary>
public class BuildDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-tests", "builds-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "harbora-tests", "outside-" + Guid.NewGuid().ToString("N"));

    public BuildDirectoryTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _outside })
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* temp — best effort */ }
    }

    private string Build(string slug, int number, long bytes = 10)
    {
        var dir = Path.Combine(_root, slug, number.ToString());
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllBytes(Path.Combine(dir, "src", "app.js"), new byte[bytes]);
        return dir;
    }

    // ---------- the pipeline removes its own ----------

    private static PipelineHarness FunctionApp()
    {
        // A function app's source is generated into the deployment's own build directory, which is
        // the path every real source takes; the harness's fake Git hands back a fixed folder instead.
        var h = new PipelineHarness(sourceType: AppSourceType.InlineCode);
        h.App.FunctionRuntime = FunctionRuntime.CSharp;
        h.Db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = h.App.Id, WorkspaceId = h.Workspace.Id, Name = "Hello", Slug = "hello",
            Trigger = FunctionTrigger.Http, Code = "// v1", IsEnabled = true
        });
        h.Db.SaveChanges();
        return h;
    }

    [Fact]
    public async Task A_successful_deployment_leaves_no_build_directory_behind()
    {
        using var h = FunctionApp();
        var directory = Path.Combine(h.WorkDir, h.App.Slug, "1");
        var existedDuringTheBuild = false;
        h.Docker.OnBuild = request => existedDuringTheBuild =
            request.ContextPath.StartsWith(directory, StringComparison.Ordinal) && Directory.Exists(request.ContextPath);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        existedDuringTheBuild.Should().BeTrue("the build reads its source from there");
        Directory.Exists(directory).Should().BeFalse("the image is the build's only product");
    }

    [Fact]
    public async Task A_failed_build_leaves_no_build_directory_behind_either()
    {
        using var h = FunctionApp();
        h.Docker.BuildFailure = new InvalidOperationException("RUN dotnet publish failed");

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        Directory.Exists(Path.Combine(h.WorkDir, h.App.Slug, "1")).Should().BeFalse();
    }

    // ---------- the sweep ----------

    private (HarboraDbContext Db, BuildDirectorySweep Sweep) Sweep()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("build-dirs-" + Guid.NewGuid()).Options);
        return (db, new BuildDirectorySweep(db,
            Options.Create(new HarboraRuntimeOptions { WorkDir = _root }), NullLogger<BuildDirectorySweep>.Instance));
    }

    private static void Deployment(HarboraDbContext db, string slug, int number, DeploymentStatus status)
    {
        var app = db.Apps.IgnoreQueryFilters().FirstOrDefault(a => a.Slug == slug);
        if (app is null)
        {
            app = new App { WorkspaceId = Guid.NewGuid(), Name = slug, Slug = slug };
            db.Apps.Add(app);
        }
        db.Deployments.Add(new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = number, Status = status
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task What_an_old_install_left_behind_is_removed_and_what_a_deployment_still_owns_is_kept()
    {
        var (db, sweep) = Sweep();
        using var _ = db;
        var finished = Build("shop", 1, bytes: 1000);
        var building = Build("shop", 2);
        var waiting = Build("shop", 3);
        var ofADeletedApp = Build("gone", 7, bytes: 500);
        Deployment(db, "shop", 1, DeploymentStatus.Succeeded);
        Deployment(db, "shop", 2, DeploymentStatus.Building);
        Deployment(db, "shop", 3, DeploymentStatus.PendingApproval);

        var result = await sweep.RunAsync(CancellationToken.None);

        Directory.Exists(finished).Should().BeFalse();
        Directory.Exists(ofADeletedApp).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "gone")).Should().BeFalse("an app's folder goes with its last build");
        Directory.Exists(building).Should().BeTrue("a deployment that is building still reads its source");
        Directory.Exists(waiting).Should().BeTrue("a deployment that has not started yet is not over");

        result.Removed.Should().Be(2);
        result.Kept.Should().Be(2);
        result.FreedBytes.Should().Be(1500);
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_the_sweep_does_not_recognise_as_a_build_is_touched()
    {
        var (db, sweep) = Sweep();
        using var _ = db;
        var upload = Path.Combine(_root, "_uploads", "shop-0199.tar.gz");
        var launch = Path.Combine(_root, "_launch", Guid.NewGuid().ToString(), "package.json");
        var notANumber = Path.Combine(_root, "shop", "cache", "keep.txt");
        var atTheRoot = Path.Combine(_root, "README");
        foreach (var file in new[] { upload, launch, notANumber, atTheRoot })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "keep");
        }

        var result = await sweep.RunAsync(CancellationToken.None);

        result.Removed.Should().Be(0);
        new[] { upload, launch, notANumber, atTheRoot }.Should().OnlyContain(f => File.Exists(f));
    }

    [Fact]
    public async Task A_sweep_with_nothing_to_do_still_says_so_in_one_line()
    {
        var (db, _) = Sweep();
        using var context = db;
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IOptions<HarboraRuntimeOptions>>(Options.Create(new HarboraRuntimeOptions { WorkDir = _root }));
        services.AddSingleton<ILogger<BuildDirectorySweep>>(NullLogger<BuildDirectorySweep>.Instance);
        services.AddScoped<BuildDirectorySweep>();
        await using var provider = services.BuildServiceProvider();
        var logger = new Lines();

        var result = await new BuildDirectorySweeper(provider.GetRequiredService<IServiceScopeFactory>(), logger, TimeSpan.Zero)
            .RunOnceAsync(CancellationToken.None);

        result!.Removed.Should().Be(0);
        logger.Written.Should().ContainSingle(l => l.StartsWith(BuildDirectorySweeper.LogPrefix))
            .Which.Should().Contain("removed 0");
    }

    private sealed class Lines : ILogger<BuildDirectorySweeper>
    {
        public List<string> Written { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Written.Add(formatter(state, exception));
    }

    // ---------- removing one ----------

    [Fact]
    public void A_link_inside_a_checkout_is_removed_and_what_it_points_at_is_not()
    {
        // A checkout is the tenant's tree, and a symbolic link in it can point anywhere on the host.
        var build = Build("shop", 4);
        var sentinel = Path.Combine(_outside, "sentinel.txt");
        File.WriteAllText(sentinel, "not the build's");
        File.SetAttributes(sentinel, FileAttributes.ReadOnly);
        LinkDirectory(Path.Combine(build, "escape"), _outside);

        BuildDirectories.Remove(_root, build);

        Directory.Exists(build).Should().BeFalse();
        File.Exists(sentinel).Should().BeTrue("the delete removes the link, never what it points at");
        File.GetAttributes(sentinel).Should().HaveFlag(FileAttributes.ReadOnly,
            "nothing reached through the link is changed either");
        File.SetAttributes(sentinel, FileAttributes.Normal);
    }

    [Fact]
    public void A_read_only_file_such_as_a_git_object_does_not_stop_the_removal()
    {
        var build = Build("shop", 5);
        var gitObject = Path.Combine(build, ".git", "objects", "ab", "cdef");
        Directory.CreateDirectory(Path.GetDirectoryName(gitObject)!);
        File.WriteAllText(gitObject, "blob");
        File.SetAttributes(gitObject, FileAttributes.ReadOnly);

        BuildDirectories.Remove(_root, build);

        Directory.Exists(build).Should().BeFalse();
    }

    [Fact]
    public void Nothing_outside_the_work_root_and_not_the_root_itself_can_be_removed()
    {
        var outsider = Path.Combine(_outside, "shop", "1");
        Directory.CreateDirectory(outsider);

        FluentActions.Invoking(() => BuildDirectories.Remove(_root, outsider)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => BuildDirectories.Remove(_root, Path.Combine(_root, "..", Path.GetFileName(_outside))))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => BuildDirectories.Remove(_root, _root)).Should().Throw<InvalidOperationException>();

        Directory.Exists(outsider).Should().BeTrue();
        Directory.Exists(_root).Should().BeTrue();
    }

    /// <summary>
    /// A directory link: a symbolic link where the system allows one, and on Windows without the
    /// privilege for that, a junction, which needs none. Either is what a checkout can contain.
    /// </summary>
    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // No symlink privilege: fall through to a junction.
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        mklink.WaitForExit();
        mklink.ExitCode.Should().Be(0, "the test needs a directory link to exist: {0}", mklink.StandardError.ReadToEnd());
        new DirectoryInfo(link).Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
    }
}
