using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Infrastructure.Learning;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The customer's page about launching (<c>docs/tutorial/15-launch.md</c>), held to the code it
/// describes, for the reason <see cref="DocumentationDriftTests"/> gives: reviewing a document tells you
/// it reads well, not that it is still true. This page in particular promises that there is almost
/// nothing to learn, so a name it gets wrong is the one thing a person learns from it.
///
/// <para>
/// What the inspector concludes is asked of the real inspector over a real folder, not of a list kept
/// here. The labels are read from the file that draws them. Each failure names the page to edit, because
/// whoever breaks one of these will be editing code and will not know a document said anything about it.
/// </para>
/// </summary>
public sealed class LaunchChapterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("harbora-launch-chapter-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static string Chapter() => File.ReadAllText(Path.Combine(TestPaths.DocsRoot, "15-launch.md"));

    private void Write(string relative, string content = "")
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private SourceInspection Inspect() => SourceInspector.Inspect(_dir.FullName);

    private static void ChapterNames(string code) =>
        Chapter().Should().Contain($"`{code}`", $"docs/tutorial/15-launch.md is meant to name {code}");

    [Fact]
    public async Task The_chapter_renders_its_tables_rather_than_printing_their_pipes()
    {
        var html = await new LearningLibrary(TestPaths.DocsRoot).ReadAsync("15-launch");

        // Pipe syntax Markdig does not recognise comes out as a paragraph of pipes: what is read, the
        // checks and the refusals are the three this page has.
        html.Should().NotBeNull();
        Regex.Matches(html!, "<table").Count.Should().Be(3);
        html.Should().NotContain("|---");
    }

    // ---- what it reads ----

    [Theory]
    [InlineData("Dockerfile", "Dockerfile", "FROM alpine", DetectedStack.Dockerfile)]
    [InlineData("package.json", "package.json", "{}", DetectedStack.Node)]
    [InlineData("*.csproj", "shop.csproj", "<Project />", DetectedStack.DotNet)]
    [InlineData("go.mod", "go.mod", "module shop", DetectedStack.Go)]
    [InlineData("composer.json", "composer.json", "{}", DetectedStack.Php)]
    [InlineData("requirements.txt", "requirements.txt", "flask", DetectedStack.Python)]
    [InlineData("pyproject.toml", "pyproject.toml", "", DetectedStack.Python)]
    [InlineData("Pipfile", "Pipfile", "", DetectedStack.Python)]
    [InlineData("index.html", "index.html", "<html></html>", DetectedStack.Static)]
    [InlineData("docker-compose.yml", "docker-compose.yml", "services: {}", DetectedStack.Compose)]
    [InlineData("build", "package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "5" } }""", DetectedStack.Frontend)]
    public void Each_file_the_chapter_says_decides_the_language_does(
        string named, string file, string content, DetectedStack stack)
    {
        ChapterNames(named);
        Write(file, content);
        if (stack == DetectedStack.Python) Write("app.py");

        Inspect().Stack.Should().Be(stack,
            $"docs/tutorial/15-launch.md says {named} decides how this builds. If that changed, edit the page's table");
    }

    [Fact]
    public void A_compose_file_beside_a_project_says_which_service_it_needs_as_the_chapter_says()
    {
        ChapterNames("image:");
        Write("Dockerfile", "FROM alpine");
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n");

        Inspect().Services.Should().ContainSingle(s => s.Kind == DetectedServiceKind.Postgres && s.Certain);
    }

    [Fact]
    public void A_procfile_worker_line_runs_in_the_background_with_that_command()
    {
        ChapterNames("Procfile");
        Write("package.json", "{}");
        Write("Procfile", "worker: node bot.js");

        var result = Inspect();
        result.Kind.Should().Be(DetectedAppKind.Worker);
        result.Port.Should().BeNull();
        result.StartCommand.Should().Be("node bot.js");
    }

    [Theory]
    [InlineData("prisma/", "package.json", "{}", "prisma/schema.prisma", "npx prisma migrate deploy")]
    [InlineData("artisan", "composer.json", "{}", "artisan", "php artisan migrate --force")]
    [InlineData("alembic.ini", "requirements.txt", "flask", "alembic.ini", "alembic upgrade head")]
    public void The_files_that_mean_tables_get_a_command_to_build_them(
        string named, string project, string content, string marker, string command)
    {
        ChapterNames(named);
        Write(project, content);
        Write(marker);
        if (project == "requirements.txt") Write("app.py");
        if (named == "prisma/") Write("prisma/migrations/0001/migration.sql");

        Inspect().ReleaseCommand.Should().Be(command);
    }

    [Fact]
    public void The_example_file_is_read_and_the_real_one_is_not()
    {
        ChapterNames(".env.example");
        ChapterNames("JWT_SECRET");
        ChapterNames("STRIPE_SECRET_KEY");
        Write("package.json", "{}");
        Write(".env.example", "JWT_SECRET=\nSTRIPE_SECRET_KEY=\n");
        Write(".env", "ONLY_IN_THE_REAL_ONE=1\n");

        var result = Inspect();
        result.EnvVars.Single(v => v.Name == "JWT_SECRET").Disposition.Should().Be(EnvVarDisposition.GeneratedSecret);
        result.EnvVars.Single(v => v.Name == "STRIPE_SECRET_KEY").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        result.EnvVars.Should().NotContain(v => v.Name == "ONLY_IN_THE_REAL_ONE",
            "docs/tutorial/15-launch.md tells a customer the inspector never opens .env");
    }

    /// <summary>The prefixes the chapter promises reach the build, and one it does not, which must not.</summary>
    [Theory]
    [InlineData("VITE_API_URL", true)]
    [InlineData("REACT_APP_API_URL", true)]
    [InlineData("STRIPE_SECRET_KEY", false)]
    public void The_public_build_variables_the_chapter_names_are_the_ones_a_build_reads(string name, bool isPublic)
    {
        if (isPublic) Chapter().Should().Contain(name[..(name.IndexOf('_') + 1)]);
        PublicBuildVariables.IsPublic(name).Should().Be(isPublic,
            "docs/tutorial/15-launch.md tells a customer which variables go into the built files");
    }

    // ---- what "connected" means ----

    [Theory]
    [InlineData("DATABASE_URL", DetectedServiceKind.Postgres)]
    [InlineData("DATABASE_URL", DetectedServiceKind.MySql)]
    [InlineData("DATABASE_URL", DetectedServiceKind.MariaDb)]
    [InlineData("PGHOST", DetectedServiceKind.Postgres)]
    [InlineData("ConnectionStrings__DefaultConnection", DetectedServiceKind.Postgres)]
    [InlineData("MONGODB_URI", DetectedServiceKind.MongoDb)]
    [InlineData("REDIS_URL", DetectedServiceKind.Redis)]
    [InlineData("AMQP_URL", DetectedServiceKind.RabbitMq)]
    [InlineData("NATS_URL", DetectedServiceKind.Nats)]
    [InlineData("MEILI_URL", DetectedServiceKind.Meilisearch)]
    public void Each_variable_the_chapter_says_an_attachment_sets_is_one_it_sets(string name, DetectedServiceKind kind)
    {
        ChapterNames(name);
        AttachEnvNames.All[kind].Should().Contain(name,
            $"docs/tutorial/15-launch.md tells a customer to read {name}. If attachments stopped setting it, edit the page");
    }

    [Theory]
    [InlineData("DB_HOST", "pg", DetectedServiceKind.Postgres)]
    [InlineData("MONGO_URI", "mongoose", DetectedServiceKind.MongoDb)]
    [InlineData("POSTGRES_PASSWORD", "pg", DetectedServiceKind.Postgres)]
    [InlineData("CACHE_URL", "redis", DetectedServiceKind.Redis)]
    public void Each_other_spelling_the_chapter_promises_is_connected_rather_than_asked(
        string name, string library, DetectedServiceKind kind)
    {
        ChapterNames(name);
        Write("package.json", $$"""{ "dependencies": { "{{library}}": "1.0.0" } }""");
        Write(".env.example", name + "=\n");

        var variable = Inspect().EnvVars.Single(v => v.Name == name);
        variable.Disposition.Should().Be(EnvVarDisposition.AliasOfAttach,
            $"docs/tutorial/15-launch.md says writing {name} in .env.example is all it takes");
        variable.Service.Should().Be(kind);
    }

    /// <summary>
    /// The two examples name no library, so these are the projects they belong to: what somebody who
    /// copied them would have, and the proposal the page is promising that person.
    /// </summary>
    [Fact]
    public void The_node_example_is_proposed_as_a_web_app_with_postgres_on_the_port_it_falls_back_to()
    {
        Chapter().Should().Contain("process.env.PORT || 3000");
        Write("package.json", """{ "dependencies": { "pg": "8.0.0" } }""");

        var result = Inspect();
        result.Stack.Should().Be(DetectedStack.Node);
        result.Kind.Should().Be(DetectedAppKind.Web);
        result.Port.Should().Be(3000);
        result.Services.Should().ContainSingle(s => s.Kind == DetectedServiceKind.Postgres && s.Certain);
    }

    [Fact]
    public void The_python_example_is_proposed_as_a_web_app_with_postgres_on_the_port_it_falls_back_to()
    {
        Chapter().Should().Contain("""os.environ.get("PORT", 5000)""");
        Write("requirements.txt", "flask\npsycopg[binary]\n");
        Write("app.py");

        var result = Inspect();
        result.Stack.Should().Be(DetectedStack.Python);
        result.Kind.Should().Be(DetectedAppKind.Web);
        result.Port.Should().Be(5000);
        result.Services.Should().ContainSingle(s => s.Kind == DetectedServiceKind.Postgres && s.Certain);
    }

    // ---- the panel's own words ----

    /// <summary>
    /// The page quotes the screen, and a customer looks for exactly these words. A label reworded in a
    /// view does not fail anything else, and leaves the page describing a button nobody can find.
    /// </summary>
    [Theory]
    [InlineData("راه انداختن اپ", "src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml")]
    [InlineData("بخوانش", "src/Harbora.Web/Views/Launch/Index.cshtml")]
    [InlineData("نشد کدت را بخوانم.", "src/Harbora.Web/Views/Launch/Index.cshtml")]
    [InlineData("مخزن خصوصی است؟", "src/Harbora.Web/Views/Launch/Index.cshtml")]
    [InlineData("اتصال Git", "src/Harbora.Web/Views/Git/Index.cshtml")]
    [InlineData("توکن این حساب را نپذیرفت", "src/Harbora.Infrastructure/Git/GitAccounts.cs")]
    [InlineData("این مخزن چند اپ دارد", "src/Harbora.Web/Views/Launch/Root.cshtml")]
    [InlineData("این را پیدا کردم", "src/Harbora.Web/Views/Launch/Confirm.cshtml")]
    [InlineData("راه بینداز", "src/Harbora.Web/Views/Launch/Confirm.cshtml")]
    [InlineData("جزئیات", "src/Harbora.Web/Views/Launch/Confirm.cshtml")]
    [InlineData("دستور پیش از اجرا", "src/Harbora.Web/Views/Launch/Confirm.cshtml")]
    [InlineData("برو به صورت‌حساب", "src/Harbora.Web/Views/Launch/_Cost.cshtml")]
    [InlineData("اپت بالاست", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("اپ بالا آمد، ولی یک چیز درست نیست", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("بالا آمد، ولی نتوانستم مطمئن شوم که کار می‌کند", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("این اپ بعداً پاک شده", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("دیتابیس را جای دیگری داری؟", "src/Harbora.Web/Views/Launch/Confirm.cshtml")]
    [InlineData("از مرورگر فقط zip می‌گیرم", "src/Harbora.Web/Infrastructure/UploadedArchive.cs")]
    [InlineData("یک ساخت هنوز در جریان است", "src/Harbora.Web/Controllers/AppsController.Upload.cs")]
    [InlineData("اینجا گیر کرد", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("دوباره امتحان کن", "src/Harbora.Web/Views/Launch/_Timeline.cshtml")]
    [InlineData("بررسی نشد", "src/Harbora.Web/Views/Shared/Design/_AppChecks.cshtml")]
    [InlineData("مشکل دارد", "src/Harbora.Web/Views/Shared/Design/_AppChecks.cshtml")]
    [InlineData("در انتظار", "src/Harbora.Web/Views/Shared/Design/_AppChecks.cshtml")]
    [InlineData("در حال اجراست", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("بالا مانده", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("مدام از کار می‌افتد", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("روی شبکهٔ این اپ جواب می‌دهد", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("آدرسش از بیرون کار می‌کند", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("در لاگش مشکلی دیده نشد", "src/Harbora.Infrastructure/Verification/AppVerifier.cs")]
    [InlineData("بررسی کن", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("اپ چطور این‌ها را می‌خواند؟", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("استقرار خودکار (Git)", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("وب‌هوک را بگذار", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("نسخه‌ی تازه", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("دستور پیش از انتشار", "src/Harbora.Web/Views/Apps/Details.cshtml")]
    [InlineData("فرم کامل", "src/Harbora.Web/Views/Apps/Index.cshtml")]
    public void Each_label_the_chapter_quotes_is_on_the_screen_it_describes(string label, string drawnBy)
    {
        Chapter().Should().Contain(label, $"docs/tutorial/15-launch.md is meant to quote «{label}»");

        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, drawnBy)).Should().Contain(label,
            $"docs/tutorial/15-launch.md tells a customer to look for «{label}». If {drawnBy} no longer " +
            "says that, edit the page along with the label");
    }

    // ---- from a terminal ----

    /// <summary>
    /// Reads the CLI's own source, the way <see cref="DocumentationDriftTests"/> reads its registrations:
    /// its commands are configured in a top-level program, so there is no surface to reflect over.
    /// </summary>
    [Fact]
    public void Each_flag_the_chapter_names_is_one_the_launch_command_takes()
    {
        var cli = string.Join("\n",
            Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, "src", "Harbora.Cli"), "*.cs")
                .Select(File.ReadAllText));
        cli.Should().Contain("AddCommand<LaunchCommand>", "the chapter has a section about a command that has to exist");

        var flags = Regex.Matches(Chapter(), @"(?<![\w-])--[a-z][a-z-]*")
            .Select(m => m.Value)
            .Where(flag => flag != "--help")
            .Distinct()
            .ToList();

        flags.Should().NotBeEmpty("the chapter has a section about the command");
        flags.Should().OnlyContain(flag => cli.Contains(flag, StringComparison.Ordinal),
            "docs/tutorial/15-launch.md names these flags, and src/Harbora.Cli must take each of them");
    }
}
