using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Characterization tests for zero-config buildpack detection. Pins detection order and per-stack
/// output before the overhaul refreshes base images / pins digests (R-BLD-1, doc 12 P7).
/// </summary>
public class BuildpackTests : IDisposable
{
    private readonly string _dir;

    public BuildpackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hbr-bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string name, string content = "")
        => File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Detects_Node_from_package_json()
    {
        Touch("package.json", "{}");
        var pack = Buildpacks.Detect(_dir, 3000);
        pack.Should().NotBeNull();
        pack!.Value.Stack.Should().Be("Node.js");
        pack.Value.Dockerfile.Should().Contain("EXPOSE 3000");
    }

    [Fact]
    public void Detects_Go_from_go_mod()
    {
        Touch("go.mod", "module x");
        Buildpacks.Detect(_dir, 8080)!.Value.Stack.Should().Be("Go");
    }

    [Fact]
    public void Detects_Python_from_requirements_with_a_recognised_entry_file()
    {
        Touch("requirements.txt", "flask");
        Touch("main.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Stack.Should().Be("Python");
    }

    [Fact]
    public void Detects_bot_py_and_it_appears_in_the_generated_CMD()
    {
        Touch("requirements.txt", "python-telegram-bot");
        Touch("bot.py");
        var pack = Buildpacks.Detect(_dir, 5000);
        pack.Should().NotBeNull();
        pack!.Value.Entry.Should().Be("bot.py");
        pack.Value.Dockerfile.Should().Contain("""CMD ["python", "bot.py"]""");
    }

    [Fact]
    public void Requirements_txt_with_no_recognisable_entry_file_returns_null_rather_than_guessing()
    {
        Touch("requirements.txt", "flask");
        Buildpacks.Detect(_dir, 5000).Should().BeNull(
            "guessing an entry file that doesn't exist produces an image that can never start");
    }

    [Fact]
    public void Main_py_still_wins_over_bot_py_when_both_exist()
    {
        Touch("requirements.txt", "flask");
        Touch("main.py");
        Touch("bot.py");
        var pack = Buildpacks.Detect(_dir, 5000);
        pack!.Value.Entry.Should().Be("main.py", "precedence is stable so an already-deployed repo keeps choosing the same file");
    }

    [Fact]
    public void Manage_py_alone_no_longer_detects_as_a_runnable_Python_app()
    {
        Touch("requirements.txt", "django");
        Touch("manage.py");
        Buildpacks.Detect(_dir, 8000).Should().BeNull(
            "`python manage.py` with no arguments prints usage and exits 0 — a crash loop, not a running app");
    }

    [Fact]
    public void Generated_Python_Dockerfile_sets_PYTHONUNBUFFERED_so_logs_are_not_silently_buffered()
    {
        Touch("requirements.txt", "flask");
        Touch("app.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Dockerfile.Should().Contain("ENV PYTHONUNBUFFERED=1");
    }

    [Fact]
    public void Worker_kind_Python_detection_claims_neither_a_port_nor_EXPOSE()
    {
        Touch("requirements.txt", "python-telegram-bot");
        Touch("bot.py");
        var df = Buildpacks.Detect(_dir, 5000, ServiceKind.Worker)!.Value.Dockerfile;
        df.Should().NotContain("EXPOSE");
        df.Should().NotContain("ENV PORT");
    }

    [Fact]
    public void Web_kind_Python_detection_claims_both_a_port_and_EXPOSE()
    {
        Touch("requirements.txt", "flask");
        Touch("app.py");
        var df = Buildpacks.Detect(_dir, 5000, ServiceKind.Web)!.Value.Dockerfile;
        df.Should().Contain("EXPOSE 5000");
        df.Should().Contain("ENV PORT=5000");
    }

    [Fact]
    public void Python_result_carries_the_entry_name_so_the_pipeline_can_print_it()
    {
        Touch("requirements.txt", "flask");
        Touch("server.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Entry.Should().Be("server.py");
    }

    [Fact]
    public void DetectionHint_names_what_was_looked_for_when_a_Python_project_has_no_entry_file()
    {
        Touch("requirements.txt", "flask");
        Buildpacks.DetectionHint(_dir).Should().Contain("app.py").And.Contain("bot.py");
    }

    [Fact]
    public void DetectionHint_calls_out_manage_py_as_Django_without_offering_to_support_it()
    {
        Touch("requirements.txt", "django");
        Touch("manage.py");
        Buildpacks.DetectionHint(_dir).Should().ContainEquivalentOf("Django");
    }

    [Fact]
    public void DetectionHint_is_null_when_nothing_looks_like_Python()
    {
        Touch("README.md", "# nothing to build");
        Buildpacks.DetectionHint(_dir).Should().BeNull();
    }

    [Fact]
    public void Detects_PHP_from_index_php()
    {
        Touch("index.php", "<?php");
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Be("PHP");
    }

    [Fact]
    public void Detects_static_site_from_index_html()
    {
        Touch("index.html", "<html></html>");
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Contain("Static");
    }

    [Fact]
    public void Node_takes_precedence_over_static_when_both_present()
    {
        Touch("package.json", "{}");
        Touch("index.html", "<html></html>");
        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js",
            "detection order is most-specific first");
    }

    [Fact]
    public void Returns_null_when_nothing_recognizable()
    {
        Touch("README.md", "# nothing to build");
        Buildpacks.Detect(_dir, 80).Should().BeNull();
    }

    // --- A package.json that is only a frontend's ------------------------------------------------

    private const string FrontendOnly = """{"scripts":{"build":"vite build"},"devDependencies":{"vite":"5"}}""";
    private const string RealNodeApp = """{"scripts":{"start":"node app.js"}}""";

    [Fact]
    public void Laravel_with_a_frontend_package_json_builds_as_PHP()
    {
        Touch("package.json", FrontendOnly);
        Touch("composer.json", "{}");
        Touch("artisan");

        var pack = Buildpacks.Detect(_dir, 80)!.Value;

        pack.Stack.Should().Be("PHP");
        pack.Dockerfile.Should().Contain("FROM php:").And.NotContain("npm");
        pack.Warning.Should().Be(NodeStartRule.AssetsNotBuilt);
    }

    [Fact]
    public void Flask_with_a_frontend_package_json_builds_as_Python()
    {
        Touch("package.json", FrontendOnly);
        Touch("requirements.txt", "flask");
        Touch("app.py");

        var pack = Buildpacks.Detect(_dir, 5000)!.Value;

        pack.Stack.Should().Be("Python");
        pack.Entry.Should().Be("app.py");
        pack.Dockerfile.Should().NotContain("npm");
        pack.Warning.Should().Be(NodeStartRule.AssetsNotBuilt);
    }

    [Fact]
    public void AspNet_with_a_frontend_package_json_builds_as_DotNet()
    {
        Touch("package.json", FrontendOnly);
        Touch("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        var pack = Buildpacks.Detect(_dir, 8080)!.Value;

        pack.Stack.Should().Be(".NET");
        pack.Dockerfile.Should().Contain("dotnet publish \"Shop.csproj\"").And.NotContain("npm");
        pack.Warning.Should().Be(NodeStartRule.AssetsNotBuilt);
    }

    [Fact]
    public void A_project_anywhere_FindCsproj_looks_counts_as_the_other_stack()
    {
        Touch("package.json", FrontendOnly);
        Directory.CreateDirectory(Path.Combine(_dir, "src", "Shop"));
        Touch(Path.Combine("src", "Shop", "Shop.csproj"), "<Project />");

        Buildpacks.Detect(_dir, 8080)!.Value.Stack.Should().Be(".NET");
    }

    [Fact]
    public void Go_with_a_frontend_package_json_builds_as_Go()
    {
        Touch("package.json", FrontendOnly);
        Touch("go.mod", "module x");

        Buildpacks.Detect(_dir, 8080)!.Value.Stack.Should().Be("Go");
    }

    [Fact]
    public void The_other_stacks_keep_their_own_order_when_a_package_json_gives_way()
    {
        Touch("package.json", FrontendOnly);
        Touch("composer.json", "{}");
        Touch("go.mod", "module x");

        Buildpacks.Detect(_dir, 8080)!.Value.Stack.Should().Be("Go");
    }

    [Fact]
    public void A_real_node_app_with_a_stray_requirements_txt_is_still_node_and_builds_exactly_as_before()
    {
        Touch("package.json", RealNodeApp);
        var before = Buildpacks.Detect(_dir, 3000)!.Value;

        Touch("requirements.txt", "flask");
        Touch("app.py");
        var after = Buildpacks.Detect(_dir, 3000)!.Value;

        after.Stack.Should().Be("Node.js");
        after.Should().Be(before);
        after.Warning.Should().BeNull();
    }

    [Fact]
    public void A_server_js_and_no_scripts_is_still_node_since_it_is_what_npm_start_runs()
    {
        Touch("package.json", "{}");
        Touch("server.js");
        Touch("composer.json", "{}");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void A_package_json_alone_with_no_start_is_still_node()
    {
        Touch("package.json", FrontendOnly);

        var pack = Buildpacks.Detect(_dir, 3000)!.Value;

        pack.Stack.Should().Be("Node.js");
        pack.Warning.Should().BeNull();
    }

    [Fact]
    public void A_package_json_with_no_start_still_outranks_its_own_index_html()
    {
        Touch("package.json", FrontendOnly);
        Touch("index.html", "<html></html>");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js",
            "index.html is not another stack's marker: in a frontend it is the source, not the site");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"scripts":"start"}""")]
    [InlineData("""{"scripts":{"start":3}}""")]
    [InlineData("""{"scripts":{"start":""}}""")]
    [InlineData("""{"scripts":{"start":"   "}}""")]
    [InlineData("""{"start":"node a.js"}""")]
    public void A_package_json_with_no_usable_start_gives_way_only_when_another_marker_exists(string content)
    {
        Touch("package.json", content);
        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");

        Touch("composer.json", "{}");
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Be("PHP");
    }

    [Fact]
    public void With_no_build_script_there_is_nothing_to_warn_about()
    {
        Touch("package.json", """{"dependencies":{"left-pad":"1"}}""");
        Touch("composer.json", "{}");

        var pack = Buildpacks.Detect(_dir, 80)!.Value;

        pack.Stack.Should().Be("PHP");
        pack.Warning.Should().BeNull();
    }

    [Fact]
    public void A_frontend_beside_python_with_no_entry_file_gets_the_python_hint_rather_than_a_node_build_that_cannot_start()
    {
        Touch("package.json", FrontendOnly);
        Touch("requirements.txt", "flask");

        Buildpacks.Detect(_dir, 5000).Should().BeNull();
        Buildpacks.DetectionHint(_dir).Should().Contain("looks like a Python project");
    }

    [Fact]
    public void A_frontend_beside_django_gets_the_django_hint()
    {
        Touch("package.json", FrontendOnly);
        Touch("requirements.txt", "django");
        Touch("manage.py");

        Buildpacks.Detect(_dir, 8000).Should().BeNull();
        Buildpacks.DetectionHint(_dir).Should().Contain("Django");
    }

    // --- What the deploy log says about it -------------------------------------------------------

    private static async Task<List<string>> DeployAndReadLog(Fakes.PipelineHarness h)
    {
        var deployment = h.QueueDeployment();
        await h.RunAsync(deployment);

        return await h.Db.DeploymentLogs.AsNoTracking()
            .Where(l => l.DeploymentId == deployment.Id)
            .OrderBy(l => l.Sequence).Select(l => l.Message).ToListAsync();
    }

    [Fact]
    public async Task The_deploy_log_names_the_stack_that_was_built_and_what_it_left_undone()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository).WithGitSource();
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), FrontendOnly);
        File.WriteAllText(Path.Combine(h.WorkDir, "composer.json"), "{}");

        var log = await DeployAndReadLog(h);

        log.Should().Contain("No Dockerfile — auto-detected PHP; using a generated build.");
        log.Should().Contain("⚠ " + NodeStartRule.AssetsNotBuilt);
        log.Should().NotContain(line => line.Contains("Node.js"));
        File.ReadAllText(Path.Combine(h.WorkDir, "Dockerfile.harbora")).Should().Contain("FROM php:");
    }

    [Fact]
    public async Task The_deploy_log_of_an_ordinary_node_app_is_what_it_always_was()
    {
        using var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository).WithGitSource();
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), RealNodeApp);

        var log = await DeployAndReadLog(h);

        log.Should().Contain("No Dockerfile — auto-detected Node.js; using a generated build.");
        log.Should().NotContain(line => line.StartsWith("⚠"));
    }

    [Fact]
    public async Task A_compose_service_built_from_source_says_the_same_under_its_own_name()
    {
        using var h = new Fakes.PipelineHarness().WithComposeFile("""
            services:
              web:
                build: .
                ports:
                  - "8080:8080"
            """);
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), FrontendOnly);
        File.WriteAllText(Path.Combine(h.WorkDir, "composer.json"), "{}");

        var log = await DeployAndReadLog(h);

        log.Should().Contain("Service 'web': detected PHP.");
        log.Should().Contain("⚠ Service 'web': " + NodeStartRule.AssetsNotBuilt);
    }
}
