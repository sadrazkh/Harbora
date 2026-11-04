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
    public void A_python_worker_named_by_its_procfile_needs_no_entry_file()
    {
        // A Procfile's `worker: python worker.py` beside a requirements.txt, the plainest worker there
        // is, was refused for having no app.py.
        Touch("requirements.txt", "redis");
        Touch("worker.py");

        var pack = Buildpacks.Detect(_dir, 5000, ServiceKind.Worker, startCommand: "python worker.py")!.Value;

        pack.Stack.Should().Be("Python");
        pack.Entry.Should().Be("python worker.py");
        pack.Dockerfile.Should().Contain("RUN pip install --no-cache-dir -r requirements.txt");
        pack.Dockerfile.Should().NotContain("CMD", "the container is started with the command, and nothing in the image would run");
        pack.Dockerfile.Should().NotContain("EXPOSE");
    }

    [Fact]
    public void A_recognised_entry_file_keeps_its_cmd_beside_a_start_command()
    {
        Touch("requirements.txt", "flask");
        Touch("app.py");

        var pack = Buildpacks.Detect(_dir, 5000, ServiceKind.Web, startCommand: "gunicorn app:app")!.Value;

        pack.Entry.Should().Be("app.py");
        pack.Dockerfile.Should().Contain("""CMD ["python", "app.py"]""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_start_command_a_python_project_with_no_entry_file_is_still_refused(string? startCommand)
    {
        Touch("requirements.txt", "redis");
        Touch("worker.py");

        Buildpacks.Detect(_dir, 5000, ServiceKind.Worker, startCommand).Should().BeNull();
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
    private const string NeitherStartNorBuild = """{"dependencies":{"left-pad":"1"}}""";

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
    public void A_package_json_alone_with_neither_start_nor_build_is_still_node()
    {
        Touch("package.json", NeitherStartNorBuild);

        var pack = Buildpacks.Detect(_dir, 3000)!.Value;

        pack.Stack.Should().Be("Node.js");
        pack.Warning.Should().BeNull();
    }

    [Fact]
    public void A_package_json_with_neither_still_outranks_its_own_index_html()
    {
        Touch("package.json", NeitherStartNorBuild);
        Touch("index.html", "<html></html>");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js",
            "index.html is not another stack's marker");
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

    // --- A frontend: built, then served ----------------------------------------------------------

    private const string ViteShaped =
        """{"name":"site","scripts":{"dev":"vite","build":"vite build","preview":"vite preview"},"devDependencies":{"vite":"5"}}""";

    private const string ReactScriptsWithNoStart =
        """{"name":"site","scripts":{"build":"react-scripts build","test":"react-scripts test"},"dependencies":{"react-scripts":"5"}}""";

    // The scripts Create React App's own template writes.
    private const string CreateReactAppTemplate =
        """{"name":"site","version":"0.1.0","private":true,"dependencies":{"react":"^18.2.0","react-dom":"^18.2.0","react-scripts":"5.0.1"},"scripts":{"start":"react-scripts start","build":"react-scripts build","test":"react-scripts test","eject":"react-scripts eject"}}""";

    [Theory]
    [InlineData(ViteShaped)]
    [InlineData(ReactScriptsWithNoStart)]
    [InlineData(CreateReactAppTemplate)]
    public void A_build_script_with_nothing_that_can_serve_is_a_frontend(string packageJson)
    {
        Touch("package.json", packageJson);
        Touch("index.html", "<html></html>");

        var pack = Buildpacks.Detect(_dir, 8123)!.Value;

        pack.Stack.Should().Be(Buildpacks.FrontendStack);
        pack.Entry.Should().Be("npm run build");
        pack.Warning.Should().BeNull();
    }

    private static string Scripts(string start, string? build = "x") =>
        build is null
            ? $$$"""{"scripts":{"start":"{{{start}}}"}}"""
            : $$$"""{"scripts":{"start":"{{{start}}}","build":"{{{build}}}"}}""";

    [Theory]
    [InlineData("react-scripts start")]
    [InlineData("react-app-rewired start")]
    [InlineData("craco start")]
    [InlineData("ng serve")]
    [InlineData("gatsby develop")]
    [InlineData("vue-cli-service serve")]
    [InlineData("vite")]
    [InlineData("webpack serve")]
    [InlineData("webpack-dev-server")]
    [InlineData("  vite  ")]
    public void A_dev_server_is_not_a_start_beside_a_build_script(string command)
    {
        Touch("package.json", Scripts(command));

        NodeStartRule.CanStart(_dir).Should().BeFalse();
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Be(Buildpacks.FrontendStack);
    }

    [Theory]
    [InlineData("react-scripts start")]
    [InlineData("ng serve")]
    [InlineData("vite")]
    public void Without_a_build_script_nothing_changes(string command)
    {
        Touch("package.json", Scripts(command, build: null));

        NodeStartRule.CanStart(_dir).Should().BeTrue();
        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Theory]
    [InlineData("ng serve --host 0.0.0.0")]
    [InlineData("vite --host")]
    [InlineData("vite preview")]
    [InlineData("react-scripts start --no-open")]
    [InlineData("gatsby develop -H 0.0.0.0")]
    [InlineData("webpack serve --host 0.0.0.0")]
    [InlineData("CI=true react-scripts start")]
    [InlineData("VITE")]
    public void Anything_but_the_exact_command_is_somebody_who_meant_it_and_stays_node(string command)
    {
        Touch("package.json", Scripts(command));

        NodeStartRule.CanStart(_dir).Should().BeTrue();
        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void A_laravel_repo_with_a_dev_server_for_a_start_is_php()
    {
        Touch("package.json", Scripts("vite", "vite build"));
        Touch("composer.json", "{}");

        var pack = Buildpacks.Detect(_dir, 80)!.Value;

        pack.Stack.Should().Be("PHP");
        pack.Warning.Should().Be(NodeStartRule.AssetsNotBuilt);
    }

    [Fact]
    public void A_server_js_beside_a_dev_server_start_still_counts_and_stays_node()
    {
        Touch("package.json", Scripts("vite"));
        Touch("server.js");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void A_dev_server_start_on_an_app_with_its_own_start_command_stays_node()
    {
        Touch("package.json", Scripts("vite"));

        Buildpacks.Detect(_dir, 3000, ServiceKind.Web, "node server/index.js")!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void The_frontend_image_builds_in_one_stage_and_serves_from_nginx_on_the_port_it_was_given()
    {
        Touch("package.json", ViteShaped);

        var dockerfile = Buildpacks.Detect(_dir, 8123)!.Value.Dockerfile;

        dockerfile.Should().Contain("FROM node:20-alpine AS build");
        dockerfile.Should().Contain("RUN npm install\n");
        dockerfile.Should().Contain("RUN npm run build\n");
        dockerfile.Should().NotContain("|| true", "a failed build has to fail this deployment");
        dockerfile.Should().NotContain("--if-present");

        dockerfile.Should().Contain("FROM nginx:alpine");
        dockerfile.Should().Contain("COPY --from=build /app/.harbora-site/ /usr/share/nginx/html/");
        dockerfile.Should().Contain("listen 8123;").And.Contain("EXPOSE 8123").And.NotContain("listen 80;");
        dockerfile.Should().Contain("try_files $uri $uri/ /index.html;");
        dockerfile.Should().Contain("autoindex off;");
        dockerfile.Should().NotContain("CMD", "nginx:alpine already runs nginx, in the foreground");
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "RUN corepack enable && pnpm install\n", "RUN pnpm run build\n", "pnpm run build")]
    [InlineData("yarn.lock", "RUN corepack enable && yarn install --frozen-lockfile || yarn install\n", "RUN yarn build\n", "yarn build")]
    public void A_frontend_builds_with_the_package_manager_its_lockfile_names(string lockfile, string install, string build, string entry)
    {
        Touch("package.json", ViteShaped);
        Touch(lockfile);

        var pack = Buildpacks.Detect(_dir, 80)!.Value;

        pack.Dockerfile.Should().Contain(install).And.Contain(build).And.NotContain("build || true");
        pack.Entry.Should().Be(entry);
    }

    [Fact]
    public void The_node_image_is_byte_for_byte_what_it_was()
    {
        Touch("package.json", RealNodeApp);

        Buildpacks.Detect(_dir, 3000)!.Value.Dockerfile.ReplaceLineEndings("\n").Should().Be(
            """
            # Auto-generated by Harbora (Node buildpack)
            FROM node:20-alpine
            WORKDIR /app
            COPY . .
            RUN npm install
            RUN npm run build --if-present
            ENV NODE_ENV=production
            ENV PORT=3000
            EXPOSE 3000
            CMD ["sh", "-c", "npm start"]
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_build_and_a_start_stays_node()
    {
        Touch("package.json", """{"scripts":{"build":"tsc","start":"node dist/index.js"}}""");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void A_build_and_a_server_js_stays_node()
    {
        Touch("package.json", """{"scripts":{"build":"tsc"}}""");
        Touch("server.js");

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void Laravel_with_vite_stays_php()
    {
        Touch("package.json", ViteShaped);
        Touch("composer.json", "{}");
        Touch("artisan");

        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Be("PHP");
    }

    [Theory]
    [InlineData(ServiceKind.Worker)]
    [InlineData(ServiceKind.Private)]
    [InlineData(ServiceKind.Cron)]
    [InlineData(ServiceKind.ReleaseTask)]
    [InlineData(ServiceKind.Static)]
    public void A_build_on_anything_but_a_web_kind_stays_node(ServiceKind kind)
    {
        Touch("package.json", ViteShaped);

        Buildpacks.Detect(_dir, 3000, kind)!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public void A_build_with_its_own_start_command_stays_node_since_npm_start_never_runs()
    {
        Touch("package.json", """{"scripts":{"build":"tsc"}}""");

        Buildpacks.Detect(_dir, 3000, ServiceKind.Web, "node dist/index.js")!.Value.Stack.Should().Be("Node.js");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_start_command_is_none_as_it_is_at_run_time(string blank)
    {
        Touch("package.json", ViteShaped);

        Buildpacks.Detect(_dir, 80, ServiceKind.Web, blank)!.Value.Stack.Should().Be(Buildpacks.FrontendStack);
    }

    [Theory]
    [InlineData("""{"scripts":{"build":""}}""")]
    [InlineData("""{"scripts":{"build":3}}""")]
    [InlineData("""{"build":"vite build"}""")]
    [InlineData("not json")]
    public void A_build_script_that_is_not_one_stays_node(string packageJson)
    {
        Touch("package.json", packageJson);

        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js");
    }

    // --- Build-time variables --------------------------------------------------------------------

    [Fact]
    public void Public_names_in_the_example_files_are_declared_with_no_value_and_nothing_else_is()
    {
        Touch("package.json", ViteShaped);
        Touch(".env.example", """
            VITE_API_URL=https://api.example.com
            REACT_APP_TITLE=Shop
            NEXT_PUBLIC_KEY=abc
            PUBLIC_ORIGIN=https://shop.example.com
            SECRET_TOKEN=hunter2
            DATABASE_URL=postgres://u:hunter2@db/x
            vite_lower=x
            """);

        var dockerfile = Buildpacks.Detect(_dir, 80)!.Value.Dockerfile;

        dockerfile.Should().Contain("ARG VITE_API_URL\n").And.Contain("ARG REACT_APP_TITLE\n")
            .And.Contain("ARG NEXT_PUBLIC_KEY\n").And.Contain("ARG PUBLIC_ORIGIN\n");
        dockerfile.Should().NotContain("SECRET_TOKEN").And.NotContain("DATABASE_URL").And.NotContain("hunter2")
            .And.NotContain("vite_lower");
        dockerfile.Should().NotContain("api.example.com", "an example's value is not a setting");
    }

    [Fact]
    public void Names_the_caller_passes_are_declared_if_public_in_a_stable_order_before_the_build()
    {
        Touch("package.json", ViteShaped);
        Touch(".env.example", "VITE_FROM_EXAMPLE=x\nVITE_BOTH=x\n");

        var dockerfile = Buildpacks.Detect(_dir, 80, ServiceKind.Web, null,
            ["VITE_FROM_APP", "VITE_BOTH", "API_SECRET", "VITE_BAD-NAME", "VITE_"])!.Value.Dockerfile;

        var args = dockerfile.Split('\n').Where(l => l.StartsWith("ARG ")).ToList();
        args.Should().Equal("ARG VITE_BOTH", "ARG VITE_FROM_APP", "ARG VITE_FROM_EXAMPLE");

        dockerfile.IndexOf("RUN npm install", StringComparison.Ordinal).Should()
            .BeLessThan(dockerfile.IndexOf("ARG VITE_BOTH", StringComparison.Ordinal), "a new value should not reinstall everything");
        dockerfile.IndexOf("ARG VITE_FROM_EXAMPLE", StringComparison.Ordinal).Should()
            .BeLessThan(dockerfile.IndexOf("RUN npm run build", StringComparison.Ordinal));
        dockerfile.LastIndexOf("ARG ", StringComparison.Ordinal).Should()
            .BeLessThan(dockerfile.IndexOf("FROM nginx", StringComparison.Ordinal), "the image that ships declares none of them");
    }

    // --- Finding the output: the real step, under a real shell -----------------------------------

    /// <summary>
    /// Runs the generated step as Docker would hand it to a shell, with its continuations joined. Null
    /// where there is no <c>sh</c>, which only a Windows machine without Git would lack.
    /// </summary>
    private (int ExitCode, string Output)? RunOutputStep() => RunStep("RUN found=", 80);

    private (int ExitCode, string Output)? RunStep(string startingWith, int port, Func<string, string>? adjust = null)
    {
        Touch("package.json", ViteShaped);
        var lines = Buildpacks.Detect(_dir, port)!.Value.Dockerfile.Split('\n')
            .SkipWhile(l => !l.StartsWith(startingWith, StringComparison.Ordinal)).ToList();

        var step = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (!line.EndsWith('\\'))
            {
                step.Append(line);
                break;
            }
            step.Append(line[..^1]);
        }

        var start = new System.Diagnostics.ProcessStartInfo("sh")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");

        // The PATH is for Git's sh on Windows, where a bare `find` can otherwise be System32's.
        var command = step.ToString()["RUN ".Length..];
        start.ArgumentList.Add("PATH=\"/usr/bin:/bin:$PATH\"; " + (adjust?.Invoke(command) ?? command));

        try
        {
            using var shell = System.Diagnostics.Process.Start(start)!;
            var output = shell.StandardOutput.ReadToEndAsync();
            var error = shell.StandardError.ReadToEndAsync();
            shell.WaitForExit();
            return (shell.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void Place(string relative, string content)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Theory]
    [InlineData("dist/index.html")]
    [InlineData("build/index.html")]
    [InlineData("out/index.html")]
    [InlineData(".output/public/index.html")]
    [InlineData("dist/shop/index.html")]
    [InlineData("dist/shop/browser/index.html")]
    [InlineData("build/client/index.html")]
    public void The_output_is_found_where_each_tool_puts_it(string index)
    {
        Place(index, index);
        Place(index.Replace("index.html", "assets/app.js"), "js");

        if (RunOutputStep() is not { } run) return;

        run.ExitCode.Should().Be(0, run.Output);
        run.Output.Should().Contain($"Serving the site the build wrote to {index.Replace("/index.html", "")}/");
        File.ReadAllText(Path.Combine(_dir, ".harbora-site", "index.html")).Should().Be(index);
        File.Exists(Path.Combine(_dir, ".harbora-site", "assets", "app.js")).Should().BeTrue();
    }

    [Fact]
    public void Top_level_output_wins_in_the_order_the_tools_are_listed_and_outranks_a_nested_one()
    {
        Place("build/index.html", "build");
        Place("dist/index.html", "dist");
        Place("dist/nested/index.html", "nested");

        if (RunOutputStep() is not { } run) return;

        run.ExitCode.Should().Be(0, run.Output);
        File.ReadAllText(Path.Combine(_dir, ".harbora-site", "index.html")).Should().Be("dist");
    }

    [Theory]
    [InlineData("gatsby-config.js")]
    [InlineData("gatsby-config.ts")]
    public void Gatsbys_public_folder_is_its_output_where_a_gatsby_config_says_so(string config)
    {
        Place("public/index.html", "gatsby");
        Place(config, "");

        if (RunOutputStep() is not { } run) return;

        run.ExitCode.Should().Be(0, run.Output);
        File.ReadAllText(Path.Combine(_dir, ".harbora-site", "index.html")).Should().Be("gatsby");
    }

    [Fact]
    public void Hidden_files_in_the_output_are_kept()
    {
        Place("dist/index.html", "site");
        Place("dist/.well-known/security.txt", "hello");

        if (RunOutputStep() is not { } run) return;

        run.ExitCode.Should().Be(0, run.Output);
        File.Exists(Path.Combine(_dir, ".harbora-site", ".well-known", "security.txt")).Should().BeTrue();
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("dist/app.js")]
    [InlineData("out/shop/index.html")]
    [InlineData(".next/server/index.html")]
    [InlineData("public/index.html")]
    [InlineData("index.html")]
    public void No_index_html_in_the_output_stops_the_build_and_says_why(string file)
    {
        Place(file, "x");

        if (RunOutputStep() is not { } run) return;

        run.ExitCode.Should().NotBe(0, "an nginx serving nothing would be reported as a success");
        run.Output.Should().Contain("The build finished without producing an index.html, so this is not a site.");
        run.Output.Should().Contain("start script").And.Contain("Dockerfile");
        Directory.Exists(Path.Combine(_dir, ".harbora-site")).Should().BeFalse();
    }

    [Fact]
    public void The_nginx_config_the_step_writes_is_the_one_meant_with_nothing_expanded_on_the_way()
    {
        if (RunStep("RUN printf", 8123, c => c.Replace("/etc/nginx/conf.d/default.conf", "written.conf")) is not { } run) return;

        run.ExitCode.Should().Be(0, run.Output);
        File.ReadAllText(Path.Combine(_dir, "written.conf")).ReplaceLineEndings("\n").Should().Be(
            """
            server {
                listen 8123;
                root /usr/share/nginx/html;
                index index.html;
                autoindex off;
                location / {
                    try_files $uri $uri/ /index.html;
                }
            }

            """.ReplaceLineEndings("\n"));
    }

    // --- Through the pipeline --------------------------------------------------------------------

    private static Fakes.PipelineHarness GitApp(string packageJson)
    {
        var h = new Fakes.PipelineHarness(sourceType: AppSourceType.GitRepository).WithGitSource();
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), packageJson);
        return h;
    }

    private static string GeneratedDockerfile(Fakes.PipelineHarness h) =>
        File.ReadAllText(Path.Combine(h.WorkDir, "Dockerfile.harbora"));

    private static void AddVariable(
        Fakes.PipelineHarness h, string key, string value, bool availableAtBuild = false, bool secret = false)
    {
        h.Db.EnvironmentVariables.Add(new Harbora.Domain.Apps.EnvironmentVariable
        {
            AppId = h.App.Id, Key = key, IsSecret = secret, AvailableAtBuild = availableAtBuild,
            Value = secret ? h.Protector.Protect(value) : value
        });
        h.Db.SaveChanges();
    }

    [Fact]
    public async Task A_vite_app_is_built_and_served_and_the_deploy_log_says_so()
    {
        using var h = GitApp(ViteShaped).WithContainerPort(8123);

        var log = await DeployAndReadLog(h);

        log.Should().Contain($"No Dockerfile — auto-detected {Buildpacks.FrontendStack} (entry: npm run build); using a generated build.");
        h.Docker.BuildRequests.Should().ContainSingle().Which.Dockerfile.Should().Be("Dockerfile.harbora");

        var dockerfile = GeneratedDockerfile(h);
        dockerfile.Should().Contain("FROM nginx:alpine").And.Contain("listen 8123;").And.Contain("EXPOSE 8123");
    }

    [Fact]
    public async Task A_public_variable_reaches_the_build_with_nobody_having_ticked_anything()
    {
        using var h = GitApp(ViteShaped);
        AddVariable(h, "VITE_API_URL", "https://api.shop.example");
        AddVariable(h, "REACT_APP_NAME", "Shop", availableAtBuild: true);
        AddVariable(h, "PUBLIC_KEY", "pk_live", secret: true);

        await DeployAndReadLog(h);

        var build = h.Docker.BuildRequests.Should().ContainSingle().Subject;
        build.BuildArgs.Should().Contain("VITE_API_URL", "https://api.shop.example");
        build.BuildArgs.Should().Contain("REACT_APP_NAME", "Shop");
        build.BuildArgs.Should().Contain("PUBLIC_KEY", "pk_live");

        var dockerfile = GeneratedDockerfile(h);
        dockerfile.Should().Contain("ARG VITE_API_URL\n").And.Contain("ARG REACT_APP_NAME\n").And.Contain("ARG PUBLIC_KEY\n");
        dockerfile.Should().NotContain("https://api.shop.example").And.NotContain("pk_live",
            "a value travels as a build arg, not written into a file on disk");
    }

    [Fact]
    public async Task A_public_variable_from_a_config_group_reaches_the_build_too()
    {
        using var h = GitApp(ViteShaped);
        var group = new Harbora.Domain.Apps.ConfigGroup { WorkspaceId = h.Workspace.Id, Name = "shared" };
        h.Db.ConfigGroups.Add(group);
        h.Db.ConfigGroupEntries.Add(new Harbora.Domain.Apps.ConfigGroupEntry
        {
            ConfigGroupId = group.Id, Key = "VITE_FROM_GROUP", Value = "yes"
        });
        h.Db.AppConfigGroups.Add(new Harbora.Domain.Apps.AppConfigGroup { AppId = h.App.Id, ConfigGroupId = group.Id, AttachOrder = 1 });
        h.Db.SaveChanges();

        await DeployAndReadLog(h);

        h.Docker.BuildRequests.Should().ContainSingle().Which.BuildArgs.Should().Contain("VITE_FROM_GROUP", "yes");
        GeneratedDockerfile(h).Should().Contain("ARG VITE_FROM_GROUP\n");
    }

    [Fact]
    public async Task A_variable_with_no_public_prefix_never_reaches_the_build_even_if_marked_for_it()
    {
        using var h = GitApp(ViteShaped);
        AddVariable(h, "SECRET_TOKEN", "hunter2", availableAtBuild: true);
        AddVariable(h, "DATABASE_PASSWORD", "hunter2", availableAtBuild: true, secret: true);
        AddVariable(h, "LOG_LEVEL", "info");
        AddVariable(h, "VITE_FINE", "yes");

        await DeployAndReadLog(h);

        h.Docker.BuildRequests.Should().ContainSingle().Which.BuildArgs.Keys.Should().Equal("VITE_FINE");
        GeneratedDockerfile(h).Should().NotContain("SECRET_TOKEN").And.NotContain("DATABASE_PASSWORD").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_typescript_backend_with_its_own_start_command_stays_node_through_the_pipeline()
    {
        using var h = GitApp("""{"scripts":{"build":"tsc"}}""").WithStartCommand("node dist/index.js");

        var log = await DeployAndReadLog(h);

        log.Should().Contain("No Dockerfile — auto-detected Node.js; using a generated build.");
        GeneratedDockerfile(h).Should().NotContain("nginx");
    }

    [Fact]
    public async Task A_worker_with_a_build_script_stays_node_through_the_pipeline()
    {
        using var h = GitApp(ViteShaped);
        h.App.Kind = ServiceKind.Worker;
        h.Db.SaveChanges();

        var log = await DeployAndReadLog(h);

        log.Should().Contain("No Dockerfile — auto-detected Node.js; using a generated build.");
    }

    [Fact]
    public async Task A_compose_service_can_be_a_frontend_on_its_own_port()
    {
        using var h = new Fakes.PipelineHarness().WithComposeFile("""
            services:
              web:
                build: .
                ports:
                  - "8123:8123"
            """);
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), ViteShaped);
        AddVariable(h, "VITE_API_URL", "https://api.shop.example");
        AddVariable(h, "SECRET_TOKEN", "hunter2", availableAtBuild: true);

        var log = await DeployAndReadLog(h);

        log.Should().Contain($"Service 'web': detected {Buildpacks.FrontendStack} (entry: npm run build).");
        GeneratedDockerfile(h).Should().Contain("listen 8123;");
        h.Docker.BuildRequests.Should().ContainSingle().Which.BuildArgs.Keys.Should().Equal("VITE_API_URL");
    }

    [Fact]
    public async Task A_compose_service_with_its_own_command_stays_node()
    {
        using var h = new Fakes.PipelineHarness().WithComposeFile("""
            services:
              web:
                build: .
                command: node dist/index.js
                ports:
                  - "8123:8123"
            """);
        File.WriteAllText(Path.Combine(h.WorkDir, "package.json"), ViteShaped);

        var log = await DeployAndReadLog(h);

        log.Should().Contain("Service 'web': detected Node.js.");
    }
}
