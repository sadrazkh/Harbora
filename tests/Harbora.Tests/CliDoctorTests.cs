using System.Net;
using System.Reflection;
using FluentAssertions;
using Harbora.Cli;
using Spectre.Console.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>harbora doctor</c>, and the preflight <c>harbora deploy</c> now runs on itself before
/// uploading anything.
///
/// <para>
/// The regression that matters, reproduced exactly: DriveUnion's own deploy failed twice because
/// <c>src/DriveUnion.Web/Scripts/build/copy-fonts.mjs</c> — an ordinary source file referenced by
/// <c>package.json</c>'s <c>fonts</c> script — never reached the server. <c>SourcePacker</c>'s
/// built-in rule that caused that is fixed elsewhere (it now only matches "build" at the project
/// root), but the exact same failure mode is still fully live through a project's own ignore file:
/// a great many JavaScript projects' <c>.gitignore</c> carries an unqualified <c>build</c> or
/// <c>dist</c> line, and <see cref="SourcePacker.DescribeExclusion"/>'s general (non-built-in)
/// ignore-pattern match still matches that at any depth, on purpose (it is what makes `ignore:
/// coverage` in harbora.yml drop `coverage/` wherever it appears). Doctor has to catch that shape,
/// not just the one built-in list that already got fixed.
/// </para>
/// </summary>
public class CliDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-doctor-" + Guid.NewGuid().ToString("N"));

    public CliDoctorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static Doctor.Check Single(IEnumerable<Doctor.Check> checks, string name) =>
        checks.Should().ContainSingle(c => c.Name == name).Which;

    // ---- manifest ---------------------------------------------------------------------------

    [Fact]
    public void No_app_anywhere_fails_with_the_servers_own_error_message()
    {
        var check = Doctor.CheckManifest(new ProjectConfig(), resolvedApp: null);

        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("No app specified", "this is the exact sentence deploy fails with today");
    }

    [Fact]
    public void An_app_from_the_command_line_satisfies_the_check_even_with_no_harbora_yml()
    {
        // Deploy resolves the app from flags/prompt/config before this ever runs; the manifest check
        // must not repeat "No app specified" for a deploy that is about to work fine.
        var check = Doctor.CheckManifest(new ProjectConfig(), resolvedApp: "my-api");

        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("my-api");
    }

    [Fact]
    public void An_app_named_in_harbora_yml_satisfies_the_check()
    {
        var config = new ProjectConfig { App = "from-config" };

        Doctor.CheckManifest(config, resolvedApp: null).Level.Should().Be(Doctor.Level.Ok);
    }

    // ---- build: context / Dockerfile / stack detection ---------------------------------------

    [Fact]
    public void A_missing_build_context_fails_outright()
    {
        var config = new ProjectConfig { Context = "does-not-exist" };

        var (checks, _) = Doctor.CheckBuild(_root, config);

        Single(checks, "Build context").Level.Should().Be(Doctor.Level.Fail);
    }

    // ---- build: root directory containment (1.2, 2026-09 market-gaps round two) ----------------

    [Fact]
    public void A_context_that_traverses_outside_the_project_is_refused_by_name()
    {
        var config = new ProjectConfig { Context = "../elsewhere" };

        var (checks, _) = Doctor.CheckBuild(_root, config);

        var check = Single(checks, "Build context");
        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("../elsewhere").And.Contain("..",
            "the refusal has to name what specifically was wrong, the way AppRootDirectory.Explain does");
    }

    [Fact]
    public void An_absolute_context_is_refused_by_name_not_reported_as_merely_missing()
    {
        // A directory that happens to exist on the machine running `harbora doctor` (unlike a plain
        // relative typo) must still be refused for being absolute — the whole point is that it never
        // named anything inside this project to begin with.
        Directory.CreateDirectory(Path.Combine(_root, "sibling"));
        var absolute = Path.Combine(_root, "sibling");
        var config = new ProjectConfig { Context = absolute };

        var (checks, _) = Doctor.CheckBuild(_root, config);

        var check = Single(checks, "Build context");
        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("absolute path");
    }

    [Fact]
    public void A_root_directory_named_after_a_packer_exclude_rule_is_refused_before_the_build_even_runs()
    {
        // Exactly the shape this task calls out: SourcePacker only excludes build/dist/target/vendor/
        // .output at the project root, but a root directory IS the project root as far as the packer
        // is concerned — every file under it would still be dropped, silently, the same way the
        // DriveUnion incident happened one layer up.
        Write("build/Dockerfile", "FROM scratch\n");
        var config = new ProjectConfig { Context = "build" };

        var (checks, _) = Doctor.CheckBuild(_root, config);

        var check = Single(checks, "Build context");
        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("build").And.Contain("excludes",
            "the check must name the rule, not just say the build failed");
    }

    [Fact]
    public void A_root_directory_named_build_two_levels_down_is_unaffected()
    {
        // The 2026-08-30 fix root-anchored the ambiguous names; a root directory that is NOT itself
        // one of them, even if a folder further down happens to be, must not be refused here.
        Write("services/api/Dockerfile", "FROM scratch\n");
        var config = new ProjectConfig { Context = "services/api" };

        var (checks, _) = Doctor.CheckBuild(_root, config);

        Single(checks, "Build context").Level.Should().Be(Doctor.Level.Ok);
    }

    [Fact]
    public void No_dockerfile_and_no_recognised_stack_fails_with_the_servers_own_error_message()
    {
        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        var check = Single(checks, "Dockerfile");
        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("the stack couldn't be auto-detected");
    }

    [Fact]
    public void A_recognised_stack_marker_with_no_dockerfile_is_fine()
    {
        Write("package.json", "{}");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        var check = Single(checks, "Dockerfile");
        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("Node");
    }

    [Fact]
    public void A_build_script_with_nothing_to_start_is_reported_as_a_frontend_and_says_what_it_cannot_see()
    {
        Write("package.json", """{"scripts":{"build":"vite build"}}""");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        var check = Single(checks, "Dockerfile");
        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("auto-detected as frontend (");
        check.Detail.Should().Contain("own start command").And.Contain("worker").And.NotContain("ENV PORT");
    }

    [Fact]
    public void A_package_json_with_neither_start_nor_build_is_reported_as_node()
    {
        Write("package.json", """{"dependencies":{"left-pad":"1"}}""");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        Single(checks, "Dockerfile").Detail.Should().Contain("auto-detected as Node (");
    }

    [Theory]
    [InlineData("composer.json", "PHP")]
    [InlineData("requirements.txt", "Python")]
    [InlineData("go.mod", "Go")]
    [InlineData("App.csproj", ".NET")]
    public void A_frontends_package_json_is_not_reported_as_the_stack_the_server_will_not_build(string marker, string stack)
    {
        Write("package.json", """{"scripts":{"build":"vite build"}}""");
        Write(marker, "");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        Single(checks, "Dockerfile").Detail.Should().Contain($"auto-detected as {stack} (");
    }

    [Theory]
    [InlineData("""{"scripts":{"start":"node a.js"}}""", "notes.txt")]
    [InlineData("{}", "server.js")]
    public void A_package_json_that_can_start_is_reported_as_node_beside_anything(string packageJson, string alongside)
    {
        Write("package.json", packageJson);
        Write(alongside, "");
        Write("composer.json", "{}");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        Single(checks, "Dockerfile").Detail.Should().Contain("auto-detected as Node (");
    }

    [Fact]
    public void Doctor_names_the_stack_the_server_builds_for_every_combination_of_markers()
    {
        // The third copy of the order, and the one that reports it before a deploy. Where the server
        // finds nothing (a Python manifest with no entry file) this check has nothing to compare:
        // doctor has always accepted the manifest alone there.
        string[] markers = ["svc/App.csproj", "go.mod", "composer.json", "requirements.txt", "main.py", "index.html", "server.js"];
        foreach (var packageJson in new[] { """{"scripts":{"start":"x"}}""", """{"scripts":{"build":"x"}}""", "broken" })
        {
            Write("package.json", packageJson);

            for (var mask = 0; mask < 1 << markers.Length; mask++)
            {
                for (var bit = 0; bit < markers.Length; bit++)
                {
                    var path = Path.Combine(_root, markers[bit].Replace('/', Path.DirectorySeparatorChar));
                    var wanted = (mask & (1 << bit)) != 0;
                    if (wanted && !File.Exists(path)) Write(markers[bit], "");
                    if (!wanted && File.Exists(path)) File.Delete(path);
                }

                if (Harbora.Infrastructure.Deployments.Buildpacks.Detect(_root, 3000) is not { } pack) continue;

                var expected = pack.Stack switch
                {
                    "Node.js" => "Node (",
                    "Static (Nginx)" => "static (",
                    Harbora.Infrastructure.Deployments.Buildpacks.FrontendStack => "frontend (",
                    var stack => stack + " ("
                };
                var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());
                Single(checks, "Dockerfile").Detail.Should().Contain($"auto-detected as {expected}",
                    $"package.json {packageJson} with {string.Join(", ", markers.Where((_, b) => (mask & (1 << b)) != 0))}");
            }
        }
    }

    [Fact]
    public void A_dotnet_project_anywhere_under_the_context_counts_as_a_recognised_stack()
    {
        Write("src/Api/Api.csproj", "<Project />");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        Single(checks, "Dockerfile").Level.Should().Be(Doctor.Level.Ok);
    }

    [Fact]
    public void An_image_deploy_skips_build_checks_entirely()
    {
        var (checks, referenced) = Doctor.CheckBuild(_root, new ProjectConfig { Image = "nginx:alpine" });

        checks.Should().ContainSingle();
        checks[0].Level.Should().Be(Doctor.Level.Ok);
        referenced.Should().BeEmpty();
    }

    [Fact]
    public void A_branch_deploy_with_no_local_dockerfile_is_not_treated_as_missing()
    {
        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig { Branch = "main" });

        Single(checks, "Dockerfile").Level.Should().Be(Doctor.Level.Ok);
    }

    [Fact]
    public void Inline_dockerfileLines_needs_no_dockerfile_file()
    {
        var config = new ProjectConfig();
        config.DockerfileLines.AddRange(["FROM node:20-alpine", "CMD [\"npm\", \"start\"]"]);

        var (checks, _) = Doctor.CheckBuild(_root, config);

        Single(checks, "Dockerfile").Level.Should().Be(Doctor.Level.Ok);
        Single(checks, "$PORT").Level.Should().Be(Doctor.Level.Warn, "these lines never mention PORT");
    }

    // ---- build: COPY sources -------------------------------------------------------------------

    [Fact]
    public void A_copy_source_that_does_not_exist_fails_with_the_line_number()
    {
        Write("Dockerfile", "FROM node:20-alpine\nCOPY missing-file.txt .\n");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        var check = Single(checks, "Dockerfile COPY");
        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("line 2").And.Contain("missing-file.txt");
    }

    [Fact]
    public void A_copy_source_that_exists_is_collected_as_referenced_but_raises_no_check()
    {
        Write("package.json", "{}");
        Write("Dockerfile", "FROM node:20-alpine\nCOPY package.json ./\n");

        var (checks, referenced) = Doctor.CheckBuild(_root, new ProjectConfig());

        checks.Should().NotContain(c => c.Name == "Dockerfile COPY");
        referenced.Should().Contain("package.json");
    }

    [Fact]
    public void A_copy_from_another_build_stage_is_not_checked_against_the_local_context()
    {
        Write("Dockerfile", "FROM node:20-alpine AS assets\nFROM scratch\nCOPY --from=assets /out/app.js .\n");

        var (checks, referenced) = Doctor.CheckBuild(_root, new ProjectConfig());

        checks.Should().NotContain(c => c.Name == "Dockerfile COPY");
        referenced.Should().BeEmpty();
    }

    [Fact]
    public void Copying_the_whole_context_with_a_dot_is_not_treated_as_a_missing_file()
    {
        Write("Dockerfile", "FROM node:20-alpine\nCOPY . .\n");

        var (checks, referenced) = Doctor.CheckBuild(_root, new ProjectConfig());

        checks.Should().NotContain(c => c.Name == "Dockerfile COPY");
        referenced.Should().BeEmpty();
    }

    // ---- build: $PORT -----------------------------------------------------------------------

    [Fact]
    public void A_dockerfile_that_never_mentions_port_warns_about_the_502()
    {
        Write("Dockerfile", "FROM nginx:alpine\nCMD [\"nginx\"]\n");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        var check = Single(checks, "$PORT");
        check.Level.Should().Be(Doctor.Level.Warn);
        check.Detail.Should().Contain("502");
    }

    [Fact]
    public void A_dockerfile_that_reads_dollar_port_passes()
    {
        // Exactly DriveUnion's own entrypoint shape.
        Write("Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:10.0\n" +
            "ENTRYPOINT [\"/bin/sh\", \"-c\", \"exec dotnet App.dll --urls http://0.0.0.0:${PORT:-8080}\"]\n");

        var (checks, _) = Doctor.CheckBuild(_root, new ProjectConfig());

        Single(checks, "$PORT").Level.Should().Be(Doctor.Level.Ok);
    }

    // ---- upload: the DriveUnion regression ---------------------------------------------------

    [Fact]
    public async Task A_source_folder_named_build_is_not_excluded_by_the_fixed_packer_alone()
    {
        // With no ignore file at all, today's fixed SourcePacker correctly keeps this — proving the
        // fixture below fails for the reason this test says it does, not because the packer is still
        // broken.
        Write("package.json", """{"scripts": {"fonts": "node Scripts/build/copy-fonts.mjs"}}""");
        Write("Scripts/build/copy-fonts.mjs", "// copies fonts");

        var checks = await Doctor.CheckUploadAsync(_root, new ProjectConfig(), [], default);

        checks.Should().NotContain(c => c.Level == Doctor.Level.Fail);
    }

    [Fact]
    public async Task An_ordinary_gitignore_entry_reproduces_the_exact_same_regression_and_doctor_catches_it()
    {
        // The regression that matters, reproduced with the building blocks the task names directly:
        // a package.json script running Scripts/build/copy-fonts.mjs, and — instead of the old
        // built-in rule, which is fixed — an entirely ordinary, extremely common .gitignore line
        // ("build") that still matches "build" at any depth via SourcePacker's general ignore-pattern
        // rule (the same rule that makes `ignore: coverage` in harbora.yml work). Doctor must warn
        // *before* upload, exactly as the task demands.
        Write("package.json",
            """{"scripts": {"prebuild": "npm run fonts", "fonts": "node Scripts/build/copy-fonts.mjs"}}""");
        Write("Scripts/build/copy-fonts.mjs", "// copies fonts");
        Write(".gitignore", "node_modules\nbuild\n");

        var checks = await Doctor.CheckUploadAsync(_root, new ProjectConfig(), [], default);

        var failure = checks.Should().ContainSingle(c => c.Level == Doctor.Level.Fail).Which;
        failure.Name.Should().Be("Upload / package.json");
        failure.Detail.Should().Contain("copy-fonts.mjs").And.Contain("fonts");
    }

    [Fact]
    public async Task Harbora_yml_ignore_entries_are_checked_the_same_way_as_gitignore()
    {
        Write("package.json", """{"scripts": {"fonts": "node Scripts/build/copy-fonts.mjs"}}""");
        Write("Scripts/build/copy-fonts.mjs", "// copies fonts");
        var config = new ProjectConfig();
        config.Ignore.Add("build");

        var checks = await Doctor.CheckUploadAsync(_root, config, [], default);

        checks.Should().Contain(c => c.Level == Doctor.Level.Fail && c.Name == "Upload / package.json");
    }

    [Fact]
    public async Task A_dockerfile_copy_source_the_upload_would_exclude_fails_the_same_way()
    {
        Write("Scripts/build/copy-fonts.mjs", "// copies fonts");
        Write(".gitignore", "build\n");
        var referenced = new List<string> { "Scripts/build/copy-fonts.mjs" };

        var checks = await Doctor.CheckUploadAsync(_root, new ProjectConfig(), referenced, default);

        var failure = checks.Should().ContainSingle(c => c.Level == Doctor.Level.Fail).Which;
        failure.Name.Should().Be("Upload / Dockerfile COPY");
    }

    [Fact]
    public async Task An_unaffected_project_reports_only_ok_and_names_what_it_would_pack()
    {
        Write("package.json", "{}");
        Write("src/index.js", "console.log(1)");

        var checks = await Doctor.CheckUploadAsync(_root, new ProjectConfig(), [], default);

        checks.Should().ContainSingle();
        var check = checks[0];
        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("2 file");
    }

    // ---- .env.local gitignore check (4.1, 2026-09-04 local-dev-parity plan) --------------------

    [Fact]
    public void No_env_local_at_all_produces_no_check()
    {
        Doctor.CheckEnvLocal(_root).Should().BeEmpty(
            "a project that has never run `harbora env pull` has nothing here worth reporting on");
    }

    [Fact]
    public void An_env_local_excluded_by_gitignore_is_ok()
    {
        Write(".env.local", "KEY=value\n");
        Write(".gitignore", "node_modules\n.env.local\n");

        var check = Single(Doctor.CheckEnvLocal(_root), "Environment");

        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("excluded");
    }

    [Fact]
    public void An_env_local_with_no_gitignore_at_all_warns()
    {
        Write(".env.local", "KEY=value\n");

        var check = Single(Doctor.CheckEnvLocal(_root), "Environment");

        check.Level.Should().Be(Doctor.Level.Warn);
        check.Detail.Should().Contain(".gitignore");
    }

    [Fact]
    public void An_env_local_with_a_gitignore_that_does_not_mention_it_warns()
    {
        Write(".env.local", "KEY=value\n");
        Write(".gitignore", "node_modules\ndist\n");

        var check = Single(Doctor.CheckEnvLocal(_root), "Environment");

        check.Level.Should().Be(Doctor.Level.Warn);
        check.Detail.Should().Contain("harbora env pull").And.Contain("secret");
    }

    [Fact]
    public void A_wildcard_env_pattern_in_gitignore_counts_as_excluding_it()
    {
        Write(".env.local", "KEY=value\n");
        Write(".gitignore", ".env*\n");

        Single(Doctor.CheckEnvLocal(_root), "Environment").Level.Should().Be(Doctor.Level.Ok);
    }

    [Fact]
    public void Env_local_being_a_built_in_upload_exclude_does_not_fake_an_ok_gitignore_result()
    {
        // .env.local is itself one of SourcePacker's AlwaysExclude names — DescribeExclusion would
        // always say "excluded" for it regardless of .gitignore, which is exactly the trap this check
        // must not fall into: an upload rule protects nothing from `git commit`.
        Write(".env.local", "KEY=value\n");
        Write(".gitignore", "node_modules\n");

        Single(Doctor.CheckEnvLocal(_root), "Environment").Level.Should().Be(Doctor.Level.Warn,
            "a built-in upload-exclusion rule must never be mistaken for a real .gitignore entry");
    }

    [Fact]
    public async Task Doctor_full_report_includes_the_env_local_check()
    {
        Write("package.json", "{}");
        Write(".env.local", "KEY=value\n");

        var checks = await DoctorCommand.RunAsync(_root, new ProjectConfig { App = "my-app" }, null, null, null, default);

        checks.Should().Contain(c => c.Name == "Environment");
    }

    // ---- auth ---------------------------------------------------------------------------------

    private sealed class Panel(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task With_no_client_at_all_the_check_fails_and_says_to_log_in()
    {
        var check = await Doctor.CheckAuthAsync(null, "https://panel.example.com");

        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("harbora login");
    }

    [Fact]
    public async Task A_working_session_reports_who_is_signed_in()
    {
        var api = new ApiClient("https://panel.example.com", "tok",
            new Panel(_ => Json(HttpStatusCode.OK, """{"email":"me@example.com"}""")));

        var check = await Doctor.CheckAuthAsync(api, "https://panel.example.com");

        check.Level.Should().Be(Doctor.Level.Ok);
        check.Detail.Should().Contain("me@example.com");
    }

    [Fact]
    public async Task An_expired_session_fails_and_says_to_log_in_again()
    {
        var api = new ApiClient("https://panel.example.com", "tok",
            new Panel(_ => Json(HttpStatusCode.Unauthorized, """{"error":"Invalid or expired token."}""")));

        var check = await Doctor.CheckAuthAsync(api, "https://panel.example.com");

        check.Level.Should().Be(Doctor.Level.Fail);
        check.Detail.Should().Contain("login");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_warning_not_a_confirmed_failure()
    {
        var api = new ApiClient("https://panel.example.com", "tok",
            new Panel(_ => throw new HttpRequestException("Connection refused")));

        var check = await Doctor.CheckAuthAsync(api, "https://panel.example.com");

        // Never fabricate an OK, but also never report a confirmed failure for a question that could
        // not actually be answered — a network hiccup is not the same fact as a refused token.
        check.Level.Should().Be(Doctor.Level.Warn);
        check.Detail.Should().Contain("not confirmed");
    }

    // ---- deploy's own preflight ---------------------------------------------------------------

    [Fact]
    public async Task Deploys_preflight_stops_before_touching_the_network_when_a_build_check_fails()
    {
        // No Dockerfile, no recognised stack — CheckBuild alone must be enough to stop this, with
        // nothing (not even an ApiClient) constructed.
        var exit = await DeployCommand.PreflightAsync(_root, new ProjectConfig(), skip: false, default);

        exit.Should().Be(1);
    }

    [Fact]
    public async Task Deploys_preflight_stops_on_the_driveunion_shaped_regression_too()
    {
        Write("package.json", """{"scripts": {"fonts": "node Scripts/build/copy-fonts.mjs"}}""");
        Write("Scripts/build/copy-fonts.mjs", "// copies fonts");
        Write(".gitignore", "build\n");

        var exit = await DeployCommand.PreflightAsync(_root, new ProjectConfig(), skip: false, default);

        exit.Should().Be(1, "this is the exact shape of the incident the preflight exists to catch");
    }

    [Fact]
    public async Task A_healthy_project_lets_the_preflight_continue()
    {
        Write("package.json", "{}");
        Write("index.js", "console.log(1)");

        var exit = await DeployCommand.PreflightAsync(_root, new ProjectConfig(), skip: false, default);

        exit.Should().BeNull();
    }

    [Fact]
    public async Task Skip_doctor_bypasses_the_preflight_entirely()
    {
        var exit = await DeployCommand.PreflightAsync(_root, new ProjectConfig(), skip: true, default);

        exit.Should().BeNull();
    }

    [Fact]
    public async Task An_image_deploy_skips_the_upload_check_in_the_preflight_too()
    {
        // Nothing is packed for an image release, so a Dockerfile-less, stack-less folder must not be
        // penalised for a build that never happens.
        var exit = await DeployCommand.PreflightAsync(_root, new ProjectConfig { Image = "nginx:alpine" }, skip: false, default);

        exit.Should().BeNull();
    }

    // ---- wiring -------------------------------------------------------------------------------

    [Fact]
    public void Doctor_takes_a_path_server_token_and_account_option()
    {
        var settings = typeof(DoctorCommand.Settings);
        settings.GetProperty(nameof(DoctorCommand.Settings.Path))!
            .GetCustomAttribute<CommandOptionAttribute>()!.LongNames.Should().Contain("path");
        settings.GetProperty(nameof(DoctorCommand.Settings.Server))!
            .GetCustomAttribute<CommandOptionAttribute>()!.LongNames.Should().Contain("server");
    }

    [Fact]
    public void Deploy_takes_a_skip_doctor_option()
    {
        typeof(DeployCommand.Settings).GetProperty(nameof(DeployCommand.Settings.SkipDoctor))!
            .GetCustomAttribute<CommandOptionAttribute>()!.LongNames.Should().Contain("skip-doctor");
    }

    [Fact]
    public void The_cli_registers_the_doctor_command()
    {
        CliSource("Program.cs").Should().Contain("AddCommand<DoctorCommand>(\"doctor\")");
    }

    [Fact]
    public void Deploy_runs_the_preflight_before_resolving_an_account_or_touching_the_network()
    {
        var source = CliSource("Commands.cs");
        var preflightCall = source.IndexOf("PreflightAsync(dir, config, settings.SkipDoctor, ct)", StringComparison.Ordinal);
        var firstNetworkUse = source.IndexOf("Session.RequireProfile(settings.Account)", StringComparison.Ordinal);

        preflightCall.Should().BeGreaterThan(-1);
        firstNetworkUse.Should().BeGreaterThan(-1);
        preflightCall.Should().BeLessThan(firstNetworkUse,
            "the whole point is reporting problems before anything is uploaded — or even before the " +
            "account used to upload it is resolved");
    }

    private static string CliSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", file));
    }
}
