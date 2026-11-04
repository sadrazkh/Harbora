using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harbora.Shared;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>A temporary folder to build a source tree in, and the helpers every inspector test wants.</summary>
public abstract class InspectorTreeTests : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "hbr-insp-" + Guid.NewGuid().ToString("N"));

    protected InspectorTreeTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    protected void Write(string relative, string content = "")
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    protected void Folder(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative));

    protected SourceInspection Inspect() => SourceInspector.Inspect(Root);

    protected static string PackageJson(params string[] dependencies) =>
        "{\"dependencies\":{" + string.Join(',', dependencies.Select(d => $"\"{d}\":\"1.0.0\"")) + "}}";

    protected static string Csproj(string sdk = "Microsoft.NET.Sdk.Web", params string[] packages) =>
        $"<Project Sdk=\"{sdk}\"><ItemGroup>" +
        string.Concat(packages.Select(p => $"<PackageReference Include=\"{p}\" Version=\"1.0.0\" />")) +
        "</ItemGroup></Project>";

    protected static IEnumerable<string> Codes(SourceInspection inspection) => inspection.Notes.Select(n => n.Code);

    protected static DetectedEnvVar Var(SourceInspection inspection, string name) =>
        inspection.EnvVars.Should().ContainSingle(v => v.Name == name, because: $"{name} should be classified").Subject;

    protected static DetectedService Service(SourceInspection inspection, DetectedServiceKind kind) =>
        inspection.Services.Should().ContainSingle(s => s.Kind == kind, because: $"{kind} should be detected").Subject;
}

/// <summary>Finding the app in the tree, and what kind of thing it is. Services and variables have their own classes.</summary>
public class SourceInspectorTests : InspectorTreeTests
{
    // --- Root directory ----------------------------------------------------------------------

    [Fact]
    public void An_app_at_the_root_has_no_root_directory()
    {
        Write("package.json", "{}");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        result.RootDirectory.Should().BeNull();
        result.RootCandidates.Should().BeEmpty();
    }

    [Fact]
    public void A_GitHub_style_single_top_level_folder_is_descended_through()
    {
        Write("repo-main/package.json", PackageJson("pg"));
        Write("repo-main/README.md");

        var result = Inspect();

        result.RootDirectory.Should().Be("repo-main");
        result.Stack.Should().Be(DetectedStack.Node);
        result.Services.Should().ContainSingle(s => s.Kind == DetectedServiceKind.Postgres);
    }

    [Fact]
    public void Single_child_folders_are_followed_all_the_way_and_the_path_uses_forward_slashes()
    {
        Write("outer/inner/go.mod", "module x");

        var result = Inspect();

        result.RootDirectory.Should().Be("outer/inner");
        result.Stack.Should().Be(DetectedStack.Go);
    }

    [Fact]
    public void The_litter_a_zip_collects_does_not_stop_the_descent()
    {
        Write("repo-main/package.json", "{}");
        Write("__MACOSX/repo-main/._package.json");
        Write(".DS_Store");
        Folder(".git");

        Inspect().RootDirectory.Should().Be("repo-main");
    }

    [Fact]
    public void A_monorepo_with_one_app_says_which_folder_it_is_in()
    {
        Write("README.md");
        Write("api/package.json", PackageJson("mongoose"));
        Write("notes/todo.txt");

        var result = Inspect();

        result.RootDirectory.Should().Be("api");
        result.RootCandidates.Should().BeEmpty();
        result.Stack.Should().Be(DetectedStack.Node);
        result.Services.Should().ContainSingle(s => s.Kind == DetectedServiceKind.MongoDb);
        Codes(result).Should().NotContain("several-apps-found");
    }

    [Fact]
    public void A_monorepo_with_several_apps_lists_them_and_chooses_none()
    {
        Write("README.md");
        Write("web/package.json", "{}");
        Write("api/go.mod", "module x");

        var result = Inspect();

        result.RootDirectory.Should().BeNull();
        result.RootCandidates.Should().Equal("api", "web");
        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Contain("several-apps-found").And.NotContain("no-stack-recognised",
            "the note that says several were found is the true one; nothing here is unrecognised");
    }

    [Fact]
    public void A_root_directory_or_candidate_is_one_the_panel_will_accept()
    {
        Write("repo-main/api/package.json", "{}");
        Write("repo-main/my web/package.json", "{}");

        var candidates = Inspect().RootCandidates;

        candidates.Should().Equal("repo-main/api", "repo-main/my web");
        candidates.Should().OnlyContain(c => AppRootDirectory.Validate(c) == PathRejection.None);
    }

    [Fact]
    public void Several_apps_inside_a_wrapper_folder_keep_the_wrapper_in_their_paths()
    {
        Write("repo-main/api/package.json", "{}");
        Write("repo-main/web/package.json", "{}");

        Inspect().RootCandidates.Should().Equal("repo-main/api", "repo-main/web");
    }

    [Fact]
    public void Everything_is_judged_from_the_chosen_root_and_not_from_above_it()
    {
        Write("README.md");
        Write(".env.example", "FROM_ABOVE=1");
        Write("api/package.json", "{}");
        Write("api/.env.example", "FROM_THE_APP=1");

        var result = Inspect();

        result.EnvVars.Select(v => v.Name).Should().Equal("FROM_THE_APP");
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("vendor")]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData(".next")]
    [InlineData(".venv")]
    [InlineData("venv")]
    [InlineData("__pycache__")]
    [InlineData("target")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".github")]
    public void A_skipped_folder_is_never_a_candidate(string folder)
    {
        Write("README.md");
        Write($"{folder}/package.json", "{}");
        Write("api/go.mod", "module x");

        var result = Inspect();

        result.RootDirectory.Should().Be("api");
        result.RootCandidates.Should().BeEmpty();
    }

    [Fact]
    public void A_folder_of_tests_is_not_a_second_app()
    {
        Write("src/Shop/Shop.csproj", Csproj());
        Write("tests/Shop.Tests/Shop.Tests.csproj", Csproj("Microsoft.NET.Sdk"));

        var result = Inspect();

        result.RootDirectory.Should().Be("src");
        result.Stack.Should().Be(DetectedStack.DotNet);
    }

    [Fact]
    public void A_site_that_lives_in_docs_is_still_found_when_nothing_else_is_there()
    {
        Write("README.md");
        Write("docs/index.html", "<html></html>");

        var result = Inspect();

        result.RootDirectory.Should().Be("docs");
        result.Stack.Should().Be(DetectedStack.Static);
    }

    [Fact]
    public void A_solution_at_the_root_keeps_the_root_however_many_projects_sit_below_it()
    {
        // Project references cross these folders, so a build started in one of them could not resolve them.
        Write("Shop.sln");
        Write("src/Shop.Web/Shop.Web.csproj", Csproj());
        Write("src/Shop.Core/Shop.Core.csproj", Csproj("Microsoft.NET.Sdk"));
        Write("tools/Migrator/Migrator.csproj", Csproj("Microsoft.NET.Sdk"));

        var result = Inspect();

        result.RootDirectory.Should().BeNull();
        result.RootCandidates.Should().BeEmpty();
        result.Stack.Should().Be(DetectedStack.DotNet);
    }

    [Fact]
    public void A_backend_and_a_frontend_are_two_apps_even_though_the_pipeline_would_find_the_project_from_above()
    {
        Write("backend/Backend.csproj", Csproj());
        Write("frontend/package.json", "{}");

        var result = Inspect();

        result.RootCandidates.Should().Equal("backend", "frontend");
        result.RootDirectory.Should().BeNull();
        result.Stack.Should().Be(DetectedStack.DotNet, "if nobody chooses, that is what a build from the root would do");
    }

    // --- Stack -------------------------------------------------------------------------------

    [Theory]
    [InlineData("package.json", DetectedStack.Node)]
    [InlineData("App.csproj", DetectedStack.DotNet)]
    [InlineData("go.mod", DetectedStack.Go)]
    [InlineData("composer.json", DetectedStack.Php)]
    [InlineData("index.php", DetectedStack.Php)]
    [InlineData("index.html", DetectedStack.Static)]
    [InlineData("index.htm", DetectedStack.Static)]
    [InlineData("docker-compose.yml", DetectedStack.Compose)]
    [InlineData("docker-compose.yaml", DetectedStack.Compose)]
    [InlineData("compose.yml", DetectedStack.Compose)]
    [InlineData("compose.yaml", DetectedStack.Compose)]
    [InlineData("Dockerfile", DetectedStack.Dockerfile)]
    [InlineData("Dockerfile.harbora", DetectedStack.Dockerfile)]
    public void Each_marker_file_means_its_stack(string file, DetectedStack expected)
    {
        Write(file, "");

        Inspect().Stack.Should().Be(expected);
    }

    [Theory]
    [InlineData("requirements.txt", "main.py")]
    [InlineData("pyproject.toml", "app.py")]
    [InlineData("Pipfile", "bot.py")]
    public void Python_needs_a_manifest_and_an_entry_file(string manifest, string entry)
    {
        Write(manifest);
        Write(entry);

        Inspect().Stack.Should().Be(DetectedStack.Python);
    }

    [Fact]
    public void A_Dockerfile_outranks_every_buildpack()
    {
        Write("Dockerfile", "FROM scratch");
        Write("package.json", "{}");
        Write("go.mod", "module x");

        Inspect().Stack.Should().Be(DetectedStack.Dockerfile);
    }

    [Fact]
    public void A_buildpack_match_outranks_a_compose_file_and_the_compose_file_only_gives_evidence()
    {
        Write("package.json", "{}");
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        Service(result, DetectedServiceKind.Postgres).Certain.Should().BeTrue();
        Codes(result).Should().NotContain("compose-runs-own-services");
    }

    [Fact]
    public void A_compose_stack_runs_its_own_services_so_none_are_proposed()
    {
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n");
        Write(".env.example", "DB_HOST=db\nDB_PASSWORD=\nLOG_LEVEL=info\n");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Compose);
        result.Services.Should().BeEmpty();
        result.ReleaseCommand.Should().BeNull();
        Codes(result).Should().Contain("compose-runs-own-services").And.NotContain("database-unclear",
            "the compose file says exactly which engine it starts");
        Var(result, "DB_HOST").Disposition.Should().Be(EnvVarDisposition.KeepExample);
        Var(result, "DB_PASSWORD").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
    }

    [Fact]
    public void Nothing_recognised_says_so()
    {
        Write("README.md", "# hello");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        result.RootDirectory.Should().BeNull();
        Codes(result).Should().Equal("no-stack-recognised");
    }

    [Fact]
    public void An_empty_folder_says_so_too()
    {
        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Equal("no-stack-recognised");
    }

    [Fact]
    public void Django_is_unsupported_by_the_buildpack_and_the_note_says_so()
    {
        Write("requirements.txt", "django");
        Write("manage.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Equal("django-unsupported");
    }

    [Fact]
    public void Django_with_a_Dockerfile_is_fine()
    {
        Write("Dockerfile", "FROM python:3.12");
        Write("requirements.txt", "django");
        Write("manage.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Dockerfile);
        Codes(result).Should().NotContain("django-unsupported");
    }

    [Fact]
    public void Django_that_happens_to_have_a_wsgi_py_still_builds_and_still_gets_the_warning()
    {
        // The buildpack runs `python wsgi.py`, which defines the application and exits.
        Write("requirements.txt", "django");
        Write("manage.py");
        Write("wsgi.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Python);
        Codes(result).Should().Contain("django-unsupported");
    }

    // --- A package.json that is only a frontend's ------------------------------------------------

    private const string FrontendOnly = """{"scripts":{"build":"vite build"},"devDependencies":{"vite":"5"}}""";
    private const string RealNodeApp = """{"scripts":{"start":"node app.js"}}""";

    [Fact]
    public void Laravel_with_a_frontend_package_json_is_php_with_its_migrations_and_a_word_about_the_assets()
    {
        Write("package.json", FrontendOnly);
        Write("composer.json", "{\"require\":{\"ext-pgsql\":\"*\"}}");
        Write("artisan");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Php);
        result.Port.Should().Be(80);
        result.ReleaseCommand.Should().Be("php artisan migrate --force");
        Service(result, DetectedServiceKind.Postgres).Certain.Should().BeTrue();
        Codes(result).Should().Contain("frontend-assets-not-built").And.NotContain("stack-ambiguous");
    }

    [Fact]
    public void Flask_with_a_frontend_package_json_is_python_on_flasks_port()
    {
        Write("package.json", FrontendOnly);
        Write("requirements.txt", "flask");
        Write("app.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Python);
        result.Port.Should().Be(5000);
        Codes(result).Should().Contain("frontend-assets-not-built").And.NotContain("stack-ambiguous");
    }

    [Fact]
    public void AspNet_with_a_frontend_package_json_is_dotnet_and_its_kind_comes_from_the_project()
    {
        Write("package.json", FrontendOnly);
        Write("Shop.csproj", Csproj("Microsoft.NET.Sdk.Worker", "Npgsql"));

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.DotNet);
        result.Kind.Should().Be(DetectedAppKind.Worker);
        Service(result, DetectedServiceKind.Postgres).Certain.Should().BeTrue();
        Codes(result).Should().Contain("frontend-assets-not-built").And.NotContain("stack-ambiguous");
    }

    [Fact]
    public void A_project_anywhere_the_pipeline_looks_counts_as_the_other_stack()
    {
        Write("package.json", FrontendOnly);
        Write("src/Shop/Shop.csproj", Csproj());

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.DotNet);
        result.RootDirectory.Should().BeNull("the package.json at the root still says the root is where the app is");
    }

    [Fact]
    public void Go_with_a_frontend_package_json_is_go()
    {
        Write("package.json", FrontendOnly);
        Write("go.mod", "module x");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Go);
        Codes(result).Should().Contain("frontend-assets-not-built");
    }

    [Fact]
    public void A_real_node_app_with_a_stray_requirements_txt_is_still_node_and_the_note_stays()
    {
        Write("package.json", RealNodeApp);
        Write("requirements.txt", "flask");
        Write("app.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        result.Port.Should().Be(3000);
        Codes(result).Should().Contain("stack-ambiguous").And.NotContain("frontend-assets-not-built");
    }

    [Fact]
    public void A_server_js_and_no_scripts_is_still_node()
    {
        Write("package.json", "{}");
        Write("server.js");
        Write("composer.json", "{}");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        Codes(result).Should().Contain("stack-ambiguous");
    }

    [Fact]
    public void A_package_json_alone_with_no_start_is_still_node_and_nothing_is_ambiguous()
    {
        Write("package.json", FrontendOnly);

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        Codes(result).Should().NotContain(["stack-ambiguous", "frontend-assets-not-built"]);
    }

    [Theory]
    [InlineData("composer.json")]
    [InlineData("index.php")]
    [InlineData("go.mod")]
    [InlineData("requirements.txt")]
    [InlineData("pyproject.toml")]
    [InlineData("Pipfile")]
    [InlineData("App.csproj")]
    [InlineData("src/App/App.csproj")]
    public void Node_that_wins_beside_another_stacks_marker_says_so(string other)
    {
        Write("package.json", RealNodeApp);
        Write(other);

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        Codes(result).Should().Contain("stack-ambiguous");
    }

    [Fact]
    public void Node_that_can_start_with_nothing_beside_it_has_nothing_to_say()
    {
        Write("package.json", RealNodeApp);

        Codes(Inspect()).Should().NotContain(["stack-ambiguous", "frontend-assets-not-built"]);
    }

    [Fact]
    public void An_index_html_is_not_another_stack()
    {
        Write("package.json", FrontendOnly);
        Write("index.html", "<html></html>");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        Codes(result).Should().NotContain("stack-ambiguous");
    }

    [Fact]
    public void A_malformed_package_json_gives_way_only_when_another_marker_exists_and_is_a_note_either_way()
    {
        Write("package.json", "not json");

        var alone = Inspect();
        alone.Stack.Should().Be(DetectedStack.Node);
        Codes(alone).Should().Contain("manifest-unreadable");

        Write("composer.json", "{}");

        var beside = Inspect();
        beside.Stack.Should().Be(DetectedStack.Php);
        Codes(beside).Should().Contain("manifest-unreadable").And.NotContain("frontend-assets-not-built");
    }

    [Fact]
    public void A_package_json_with_no_build_script_has_no_assets_to_warn_about()
    {
        Write("package.json", "{\"dependencies\":{\"left-pad\":\"1\"}}");
        Write("composer.json", "{}");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Php);
        Codes(result).Should().NotContain(["stack-ambiguous", "frontend-assets-not-built"]);
    }

    [Fact]
    public void A_dockerfile_builds_what_it_likes_so_the_assets_are_not_mentioned()
    {
        Write("Dockerfile", "FROM x");
        Write("package.json", FrontendOnly);
        Write("composer.json", "{}");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Dockerfile);
        Codes(result).Should().NotContain(["stack-ambiguous", "frontend-assets-not-built"]);
    }

    [Fact]
    public void A_frontend_beside_django_gets_the_django_note_and_nothing_about_assets()
    {
        Write("package.json", FrontendOnly);
        Write("requirements.txt", "django");
        Write("manage.py");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Equal("django-unsupported");
    }

    [Fact]
    public void A_frontend_beside_python_with_no_entry_file_says_what_is_missing()
    {
        Write("package.json", FrontendOnly);
        Write("requirements.txt", "flask");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Equal("no-stack-recognised");
    }

    // --- Kind and start command --------------------------------------------------------------

    [Fact]
    public void A_procfile_web_line_means_web()
    {
        Write("requirements.txt", "aiogram");
        Write("bot.py");
        Write("Procfile", "web: python bot.py\nworker: python jobs.py\n");

        var result = Inspect();

        result.Kind.Should().Be(DetectedAppKind.Web);
        result.StartCommand.Should().BeNull();
    }

    [Fact]
    public void A_procfile_with_only_a_worker_line_means_worker_with_that_line_as_the_start_command()
    {
        Write("requirements.txt", "flask");
        Write("app.py");
        Write("Procfile", "# jobs\nworker:   python jobs.py --queue=default  \n");

        var result = Inspect();

        result.Kind.Should().Be(DetectedAppKind.Worker);
        result.StartCommand.Should().Be("python jobs.py --queue=default");
        result.Port.Should().BeNull();
    }

    [Fact]
    public void A_procfile_naming_neither_leaves_the_decision_to_the_rest()
    {
        Write("package.json", PackageJson("telegraf"));
        Write("Procfile", "clock: node clock.js\n");

        Inspect().Kind.Should().Be(DetectedAppKind.Worker);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk.Worker", DetectedAppKind.Worker)]
    [InlineData("Microsoft.NET.Sdk.Web", DetectedAppKind.Web)]
    public void The_dotnet_sdk_says_which_kind(string sdk, DetectedAppKind expected)
    {
        Write("App.csproj", Csproj(sdk));

        Inspect().Kind.Should().Be(expected);
    }

    [Fact]
    public void The_kind_is_that_of_the_project_the_pipeline_would_publish()
    {
        Write("Shop.sln");
        Write("Jobs/Jobs.csproj", Csproj("Microsoft.NET.Sdk.Worker"));
        Write("ShopApi/ShopApi.csproj", Csproj());

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Theory]
    [InlineData("python-telegram-bot")]
    [InlineData("aiogram")]
    [InlineData("pyTelegramBotAPI")]
    [InlineData("telethon")]
    [InlineData("discord.py")]
    [InlineData("celery")]
    public void A_python_bot_or_consumer_library_with_no_web_framework_is_a_worker(string library)
    {
        Write("requirements.txt", library + "==1.0");
        Write("main.py");

        var result = Inspect();

        result.Kind.Should().Be(DetectedAppKind.Worker);
        result.Port.Should().BeNull();
    }

    [Theory]
    [InlineData("flask")]
    [InlineData("django")]
    [InlineData("fastapi")]
    [InlineData("uvicorn")]
    [InlineData("gunicorn")]
    [InlineData("aiohttp")]
    [InlineData("starlette")]
    public void A_python_web_framework_beside_it_keeps_it_web(string framework)
    {
        Write("requirements.txt", $"celery\n{framework}\n");
        Write("main.py");

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Theory]
    [InlineData("telegraf")]
    [InlineData("grammy")]
    [InlineData("node-telegram-bot-api")]
    [InlineData("discord.js")]
    public void A_node_bot_library_with_no_web_framework_is_a_worker(string library)
    {
        Write("package.json", PackageJson(library));

        var result = Inspect();

        result.Kind.Should().Be(DetectedAppKind.Worker);
        result.Port.Should().BeNull();
    }

    [Theory]
    [InlineData("express")]
    [InlineData("fastify")]
    [InlineData("koa")]
    [InlineData("next")]
    [InlineData("nuxt")]
    [InlineData("@nestjs/core")]
    [InlineData("hono")]
    public void A_node_web_framework_beside_it_keeps_it_web(string framework)
    {
        Write("package.json", PackageJson("telegraf", framework));

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Fact]
    public void Bullmq_alone_is_a_producers_library_as_much_as_a_consumers_and_stays_web()
    {
        Write("package.json", PackageJson("bullmq"));

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Theory]
    [InlineData("{\"dependencies\":{\"bullmq\":\"1\"},\"scripts\":{\"worker\":\"node jobs.js\"}}", null)]
    [InlineData("{\"dependencies\":{\"bullmq\":\"1\"},\"scripts\":{\"start\":\"node src/worker.js\"}}", null)]
    [InlineData("{\"dependencies\":{\"bullmq\":\"1\"},\"main\":\"dist/worker.js\"}", null)]
    [InlineData("{\"dependencies\":{\"bullmq\":\"1\"}}", "worker.js")]
    public void Bullmq_with_a_worker_script_is_a_worker(string packageJson, string? file)
    {
        Write("package.json", packageJson);
        if (file is not null) Write(file);

        Inspect().Kind.Should().Be(DetectedAppKind.Worker);
    }

    [Fact]
    public void Bullmq_with_a_worker_script_and_a_web_framework_stays_web()
    {
        Write("package.json", "{\"dependencies\":{\"bullmq\":\"1\",\"express\":\"4\"},\"scripts\":{\"worker\":\"node w.js\"}}");

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Fact]
    public void A_bot_library_only_a_developer_installs_does_not_make_a_worker()
    {
        Write("package.json", "{\"devDependencies\":{\"telegraf\":\"1\"}}");

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    [Fact]
    public void A_web_framework_anywhere_keeps_it_web_because_a_wrong_worker_gets_no_address()
    {
        Write("package.json", "{\"dependencies\":{\"telegraf\":\"1\"},\"devDependencies\":{\"express\":\"4\"}}");

        Inspect().Kind.Should().Be(DetectedAppKind.Web);
    }

    // --- Port --------------------------------------------------------------------------------

    [Theory]
    [InlineData("EXPOSE 8123", 8123)]
    [InlineData("expose 8123/tcp", 8123)]
    [InlineData("EXPOSE 8123 9000", 8123)]
    [InlineData("EXPOSE 53/udp 8123", 8123)]
    [InlineData("ENV PORT=8123\nEXPOSE $PORT", 8123)]
    [InlineData("ENV PORT 8123\nEXPOSE ${PORT}", 8123)]
    [InlineData("ARG PORT=8123\nEXPOSE ${PORT}/tcp", 8123)]
    [InlineData("EXPOSE ${PORT:-8123}", 8123)]
    [InlineData("EXPOSE \\\n  8123", 8123)]
    public void Expose_gives_the_port(string line, int expected)
    {
        Write("Dockerfile", $"FROM x\n{line}\nCMD y\n");

        Inspect().Port.Should().Be(expected);
    }

    [Fact]
    public void An_expose_in_a_build_stage_says_nothing_about_the_running_image()
    {
        Write("Dockerfile", "FROM a AS build\nEXPOSE 1111\nFROM b\nEXPOSE 2222\n");

        Inspect().Port.Should().Be(2222);
    }

    [Fact]
    public void Expose_outranks_the_example_file()
    {
        Write("Dockerfile", "FROM x\nEXPOSE 8123\n");
        Write(".env.example", "PORT=9000");

        Inspect().Port.Should().Be(8123);
    }

    [Fact]
    public void The_example_file_outranks_the_stacks_default()
    {
        Write("package.json", "{}");
        Write(".env.example", "PORT=4100");

        Inspect().Port.Should().Be(4100);
    }

    [Theory]
    [InlineData("PORT=")]
    [InlineData("PORT=abc")]
    [InlineData("PORT=0")]
    [InlineData("PORT=70000")]
    [InlineData("PORT=${PORT}")]
    public void A_port_in_the_example_file_that_is_not_a_port_is_ignored(string line)
    {
        Write("package.json", "{}");
        Write(".env.example", line);

        Inspect().Port.Should().Be(3000);
    }

    [Theory]
    [InlineData("package.json", 3000)]
    [InlineData("App.csproj", 8080)]
    [InlineData("go.mod", 8080)]
    [InlineData("composer.json", 80)]
    [InlineData("index.html", 80)]
    public void Each_buildpack_has_a_default(string marker, int expected)
    {
        Write(marker, "");

        Inspect().Port.Should().Be(expected);
    }

    [Fact]
    public void A_static_site_is_port_80_whatever_the_example_file_says_because_the_generated_nginx_never_reads_it()
    {
        Write("index.html", "<html></html>");
        Write(".env.example", "PORT=3000");

        Inspect().Port.Should().Be(80);
    }

    [Theory]
    [InlineData("flask", 5000)]
    [InlineData("uvicorn", 8000)]
    [InlineData("fastapi", 8000)]
    [InlineData("gunicorn", 8000)]
    [InlineData("requests", 8000)]
    public void Python_under_the_buildpack_proposes_the_frameworks_own_port_since_the_proposal_is_what_PORT_is_set_to(string library, int expected)
    {
        Write("requirements.txt", library);
        Write("app.py");

        Inspect().Port.Should().Be(expected);
    }

    [Theory]
    [InlineData("package.json", "{\"dependencies\":{\"next\":\"14\"}}", 3000)]
    [InlineData("package.json", "{\"dependencies\":{\"express\":\"4\"}}", 3000)]
    [InlineData("requirements.txt", "uvicorn", 8000)]
    [InlineData("requirements.txt", "flask", 5000)]
    public void A_dockerfile_with_no_expose_takes_the_frameworks_well_known_default(string file, string content, int expected)
    {
        Write("Dockerfile", "FROM x");
        Write(file, content);

        Inspect().Port.Should().Be(expected);
    }

    [Fact]
    public void A_dockerfile_with_no_expose_and_no_framework_has_no_number_to_offer()
    {
        Write("Dockerfile", "FROM x");

        Inspect().Port.Should().BeNull();
    }

    [Fact]
    public void A_worker_has_no_port_even_when_the_dockerfile_exposes_one()
    {
        Write("Dockerfile", "FROM x\nEXPOSE 8123\n");
        Write("Procfile", "worker: python w.py");

        Inspect().Port.Should().BeNull();
    }

    // --- Release command ---------------------------------------------------------------------

    [Fact]
    public void Prisma_with_migrations_has_a_release_command()
    {
        Write("package.json", PackageJson("prisma"));
        Write("prisma/schema.prisma", "");
        Write("prisma/migrations/0001_init/migration.sql");

        Inspect().ReleaseCommand.Should().Be("npx prisma migrate deploy");
    }

    [Fact]
    public void Prisma_with_no_migrations_has_none()
    {
        Write("package.json", PackageJson("prisma"));
        Write("prisma/schema.prisma", "");

        Inspect().ReleaseCommand.Should().BeNull();
    }

    [Fact]
    public void An_artisan_file_has_a_release_command()
    {
        Write("composer.json", "{}");
        Write("artisan");

        Inspect().ReleaseCommand.Should().Be("php artisan migrate --force");
    }

    [Fact]
    public void An_alembic_ini_has_a_release_command()
    {
        Write("requirements.txt", "alembic");
        Write("app.py");
        Write("alembic.ini");

        Inspect().ReleaseCommand.Should().Be("alembic upgrade head");
    }

    [Fact]
    public void A_dockerfile_can_hold_any_tool()
    {
        Write("Dockerfile", "FROM x");
        Write("alembic.ini");

        Inspect().ReleaseCommand.Should().Be("alembic upgrade head");
    }

    [Fact]
    public void No_tooling_means_no_release_command()
    {
        Write("package.json", "{}");

        Inspect().ReleaseCommand.Should().BeNull();
    }

    // --- A bad tree --------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Z:/definitely/not/here")]
    public void A_missing_directory_is_nothing_and_a_note(string root)
    {
        var result = SourceInspector.Inspect(root);

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Equal("source-unreadable");
    }

    [Fact]
    public void A_file_given_as_the_root_is_nothing_and_a_note()
    {
        Write("package.json", "{}");

        Codes(SourceInspector.Inspect(Path.Combine(Root, "package.json"))).Should().Equal("source-unreadable");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"dependencies\":")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("")]
    public void A_malformed_package_json_is_a_note_and_the_stack_still_stands(string content)
    {
        Write("package.json", content);

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node);
        result.Services.Should().BeEmpty();
        result.Notes.Should().ContainSingle(n => n.Code == "manifest-unreadable" && n.Message.Contains("package.json"));
    }

    [Fact]
    public void Odd_but_valid_json_is_read_without_complaint()
    {
        Write("package.json", "// a comment\n{\"dependencies\":{\"pg\":\"1\",},\"scripts\":3,\"main\":false,\"devDependencies\":[]}");

        var result = Inspect();

        Service(result, DetectedServiceKind.Postgres).Certain.Should().BeTrue();
        Codes(result).Should().NotContain("manifest-unreadable");
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("composer.json")]
    [InlineData("go.mod")]
    [InlineData("requirements.txt")]
    [InlineData("pyproject.toml")]
    [InlineData("Pipfile")]
    [InlineData("App.csproj")]
    [InlineData("Dockerfile")]
    [InlineData("Procfile")]
    [InlineData(".env.example")]
    [InlineData("prisma/schema.prisma")]
    [InlineData("docker-compose.yml")]
    public void Binary_junk_in_any_file_it_reads_is_survived(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, file))!);
        var junk = new byte[4096];
        new Random(42).NextBytes(junk);
        File.WriteAllBytes(Path.Combine(Root, file), junk);
        Write("main.py");

        var act = () => Inspect();

        act.Should().NotThrow();
        Codes(act()).Should().NotContain("source-unreadable", "one bad file should not cost the rest of the tree");
    }

    [Fact]
    public void Malformed_xml_in_a_project_file_is_a_note_and_the_stack_still_stands()
    {
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><ItemGroup>");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.DotNet);
        result.Kind.Should().Be(DetectedAppKind.Web);
        Codes(result).Should().Contain("manifest-unreadable");
    }

    [Fact]
    public void A_project_file_with_a_doctype_is_refused_rather_than_expanded()
    {
        Write("App.csproj",
            "<!DOCTYPE Project [<!ENTITY pkg \"Npgsql\">]><Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"&pkg;\" /></ItemGroup></Project>");

        var result = Inspect();

        result.Services.Should().BeEmpty();
        Codes(result).Should().Contain("manifest-unreadable");
    }

    [Fact]
    public void A_2_MiB_manifest_is_not_read()
    {
        var padding = new string('a', 2 * 1024 * 1024);
        Write("package.json", "{\"dependencies\":{\"pg\":\"1\"},\"x\":\"" + padding + "\"}");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Node, "that it is there is enough to know the stack");
        result.Services.Should().BeEmpty();
        Codes(result).Should().Contain("manifest-unreadable");
    }

    [Fact]
    public void A_file_of_exactly_the_limit_is_read()
    {
        var content = "{\"dependencies\":{\"pg\":\"1\"},\"x\":\"\"}";
        var padded = content.Insert(content.Length - 2, new string('a', 1024 * 1024 - content.Length));
        Encoding.UTF8.GetByteCount(padded).Should().Be(1024 * 1024);
        Write("package.json", padded);

        Service(Inspect(), DetectedServiceKind.Postgres).Certain.Should().BeTrue();
    }

    [Fact]
    public void A_2_MiB_example_file_is_not_read()
    {
        Write("package.json", "{}");
        Write(".env.example", "BIG=" + new string('a', 2 * 1024 * 1024));

        var result = Inspect();

        result.EnvVars.Should().BeEmpty();
        Codes(result).Should().Contain("manifest-unreadable");
    }

    [Fact]
    public void A_tree_deeper_than_the_limit_says_the_search_stopped_rather_than_saying_nothing_is_there()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", 20));
        Write($"{deep}/App.csproj", Csproj());
        Write("README.md");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Contain("tree-too-large");
    }

    [Fact]
    public void A_tree_with_too_many_entries_says_the_search_stopped_rather_than_saying_nothing_is_there()
    {
        Directory.CreateDirectory(Path.Combine(Root, "a-junk"));
        for (var i = 0; i < 20_100; i++)
            File.Create(Path.Combine(Root, "a-junk", $"{i}.txt")).Dispose();
        Write("README.md");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Unknown);
        Codes(result).Should().Contain("tree-too-large");

        // The same tree with no start script is still undecided as far as the search could see, and
        // says so. With one, the search only ever fed a note, so an upload carrying node_modules is
        // not told it is too large.
        Write("package.json", "{}");
        Codes(Inspect()).Should().Contain("tree-too-large");

        Write("package.json", RealNodeApp);
        var node = Inspect();
        node.Stack.Should().Be(DetectedStack.Node);
        Codes(node).Should().NotContain("tree-too-large");
    }

    [Fact]
    public void A_folder_that_is_a_link_is_not_read_through()
    {
        Write("outside/prisma/schema.prisma", "datasource db {\n  provider = \"postgresql\"\n}\n");
        Write("app/package.json", "{}");
        LinkFolder(Path.Combine(Root, "app", "prisma"), Path.Combine(Root, "outside", "prisma"));

        SourceInspector.Inspect(Path.Combine(Root, "app")).Services.Should().BeEmpty();
    }

    [Fact]
    public void The_search_for_projects_does_not_walk_through_a_folder_that_is_a_link()
    {
        Write("outside/Elsewhere.csproj", Csproj());
        Write("app/README.md");
        LinkFolder(Path.Combine(Root, "app", "linked"), Path.Combine(Root, "outside"));

        var result = SourceInspector.Inspect(Path.Combine(Root, "app"));

        result.Stack.Should().Be(DetectedStack.Unknown);
        result.RootDirectory.Should().BeNull("a link is not one of this tree's own folders");
    }

    private static void LinkFolder(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction, because a symbolic link on Windows needs a privilege a developer may not
            // have, and a test that quietly skips itself proves nothing.
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            mklink.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        new DirectoryInfo(link).LinkTarget.Should().NotBeNull("the test is about a link, so there has to be one");
    }

    [Fact]
    public void An_example_file_that_is_a_link_is_not_followed_even_to_somewhere_inside_the_tree()
    {
        // This one returns early where the user may not create a file link, which on a developer's
        // Windows machine usually means it proves nothing; a folder link above covers the same code.
        var linkParent = Path.Combine(Root, "app");
        Directory.CreateDirectory(linkParent);
        File.WriteAllText(Path.Combine(linkParent, "go.mod"), "module x");
        File.WriteAllText(Path.Combine(linkParent, "secret.txt"), "REAL=do-not-read");
        if (!TryLinkFile(Path.Combine(linkParent, ".env.example"), Path.Combine(linkParent, "secret.txt"))) return;

        var result = SourceInspector.Inspect(linkParent);

        result.EnvVars.Should().BeEmpty("a link is never followed, even to a file inside the tree");
        Codes(result).Should().Contain("manifest-unreadable");
    }

    private static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- Time, on files built to take a long time --------------------------------------------

    public static TheoryData<string, string> SlowInputs()
    {
        // Each has the shape that makes a careless pattern or a string built up in a loop quadratic,
        // which at a mebibyte is the difference between milliseconds and the job timing out.
        var spaces = new string(' ', 900_000);
        var shortLines = string.Concat(Enumerable.Repeat("a\n", 450_000));
        return new TheoryData<string, string>
        {
            { "Procfile", $"worker: x{spaces}y" },
            { "docker-compose.yml", $"  image: x{spaces}y" },
            { "pyproject.toml", $"[a{spaces}" },
            { "Pipfile", $"[a{spaces}" },
            { "requirements.txt", $"a{spaces}" },
            { ".env.example", $"FOO=\"unclosed\n{shortLines}" },
            { ".env.example", $"FOO=\"closed at the very end\n{shortLines}\"" },
            { "docker-compose.dev.yml", string.Concat(Enumerable.Range(0, 40_000).Select(i => $"image: postgres:{i}\n")) }
        };
    }

    [Theory]
    [MemberData(nameof(SlowInputs))]
    public void A_file_built_to_be_slow_is_not(string file, string content)
    {
        Write("package.json", "{}");
        Write(file, content);

        var watch = Stopwatch.StartNew();
        Inspect();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "a quadratic read of this file would take minutes");
    }

    [Fact]
    public void Evidence_stays_short_enough_to_read()
    {
        Write("package.json", "{}");
        Write("docker-compose.dev.yml", string.Concat(Enumerable.Range(0, 100).Select(i => $"image: postgres:{i}\n")));

        Service(Inspect(), DetectedServiceKind.Postgres).Evidence.Should().HaveCount(8);
    }

    // --- The result itself -------------------------------------------------------------------

    [Fact]
    public void The_result_survives_the_trip_through_the_row_it_is_stored_on()
    {
        Write("package.json", PackageJson("pg"));
        Write(".env.example", "DB_HOST=x\nJWT_SECRET=y\nSTRIPE_KEY=\nLOG_LEVEL=info\n");
        Write("worker.js");

        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var result = Inspect();

        var back = JsonSerializer.Deserialize<SourceInspection>(JsonSerializer.Serialize(result, options), options)!;

        back.Stack.Should().Be(result.Stack);
        back.Port.Should().Be(result.Port);
        back.Services.Should().ContainSingle().Which.Evidence.Should().Equal(result.Services[0].Evidence);
        back.EnvVars.Should().Equal(result.EnvVars);
    }

    // --- The notes themselves ----------------------------------------------------------------

    [Fact]
    public void Every_note_speaks_both_languages()
    {
        Write("package.json", "broken");
        Write("composer.json", "{}");
        Write(".env", "A=1");
        Write(".env.example", "DB_HOST=x\nDATABASE_URL=sqlite:///x.db");
        Write("docs/x");

        var result = Inspect();

        result.Notes.Should().NotBeEmpty();
        result.Notes.Should().OnlyContain(n =>
            n.Message.Length > 0 && n.MessageFa.Any(c => c >= '\u0600' && c <= '\u06FF'));
    }
}
