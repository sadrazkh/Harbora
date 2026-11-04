using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The inspector lives in <c>Harbora.Shared</c>, which cannot see the two things it has to agree with,
/// so it holds a copy of each. These run the copies against the originals. If one of them fails, the
/// screen is about to promise something the deploy will not do; change the copy, not the test.
/// </summary>
public class SourceInspectorParityTests : InspectorTreeTests
{
    private static DetectedStack FromBuildpack(BuildpackResult? pack) => pack?.Stack switch
    {
        null => DetectedStack.Unknown,
        "Node.js" => DetectedStack.Node,
        ".NET" => DetectedStack.DotNet,
        "Go" => DetectedStack.Go,
        "PHP" => DetectedStack.Php,
        "Python" => DetectedStack.Python,
        "Static (Nginx)" => DetectedStack.Static,
        var other => throw new InvalidOperationException($"A new buildpack, {other}: teach the inspector about it.")
    };

    private void InspectorShouldAgreeWithThePipeline(string because)
    {
        var inspection = Inspect();
        var judged = inspection.RootDirectory is null ? Root : Path.Combine(Root, inspection.RootDirectory);

        inspection.Stack.Should().Be(FromBuildpack(Buildpacks.Detect(judged, 3000)), because);
    }

    // --- Buildpacks.Detect -------------------------------------------------------------------

    [Fact]
    public void Every_combination_of_marker_files_detects_the_same_stack()
    {
        // Every marker with every other, which is what order bugs are made of. Ten files make 1024
        // trees, one folder is reused for all of them, and a hand-picked list would only ever hold
        // the combinations somebody thought of.
        string[] markers =
        [
            "package.json", "svc/App.csproj", "go.mod", "composer.json", "index.php",
            "requirements.txt", "Pipfile", "main.py", "manage.py", "index.html"
        ];
        Folder("svc");

        for (var mask = 0; mask < 1 << markers.Length; mask++)
        {
            for (var bit = 0; bit < markers.Length; bit++)
            {
                var path = Path.Combine(Root, markers[bit]);
                var wanted = (mask & (1 << bit)) != 0;
                if (wanted && !File.Exists(path)) File.WriteAllText(path, "");
                if (!wanted && File.Exists(path)) File.Delete(path);
            }

            InspectorShouldAgreeWithThePipeline(
                "with " + string.Join(", ", markers.Where((_, bit) => (mask & (1 << bit)) != 0)));
        }
    }

    [Theory]
    [InlineData("app.py")]
    [InlineData("main.py")]
    [InlineData("wsgi.py")]
    [InlineData("server.py")]
    [InlineData("bot.py")]
    [InlineData("run.py")]
    [InlineData("start.py")]
    [InlineData("__main__.py")]
    [InlineData("manage.py")]
    [InlineData("application.py")]
    public void The_same_python_entry_files_count(string entry)
    {
        Write("requirements.txt");
        Write(entry);

        InspectorShouldAgreeWithThePipeline(entry);
    }

    [Theory]
    [InlineData("requirements.txt")]
    [InlineData("pyproject.toml")]
    [InlineData("Pipfile")]
    public void A_python_manifest_with_no_entry_file_stops_detection_even_with_an_index_html_beside_it(string manifest)
    {
        Write(manifest);
        Write("index.html");

        InspectorShouldAgreeWithThePipeline(manifest);
        Inspect().Stack.Should().Be(DetectedStack.Unknown);
    }

    [Theory]
    [InlineData("index.htm")]
    [InlineData("pyproject.toml")]
    public void The_markers_outside_the_exhaustive_test_agree_too(string file)
    {
        Write(file);
        Write("main.py");

        InspectorShouldAgreeWithThePipeline(file);
    }

    [Theory]
    [InlineData("bin/Debug/App.csproj")]
    [InlineData("deep/obj/App.csproj")]
    [InlineData("vendor/lib/Lib.csproj")]
    [InlineData("node_modules/x/windows/X.csproj")]
    [InlineData("dist/App.csproj")]
    [InlineData(".hidden/App.csproj")]
    public void A_project_file_is_looked_for_exactly_where_the_pipeline_looks(string project)
    {
        // Only bin and obj are left out. A project under vendor/ really is what the pipeline will
        // build, ahead of the go.mod at the root, so saying Go here would be the inspector lying.
        Write("go.mod", "module x");
        Write(project, Csproj());

        InspectorShouldAgreeWithThePipeline(project);
    }

    public static TheoryData<string[]> ProjectTrees() => new()
    {
        new[] { "Top.csproj", "b/Zed.csproj", "a/c/Deep.csproj" },
        new[] { "zeta/Z.csproj", "alpha/A.csproj" },
        new[] { "alpha/deep/er/A.csproj", "zeta/Z.csproj" },
        new[] { "tools/Helper.csproj", "src/MyApi/MyApi.csproj" },
        new[] { "tools/Helper.csproj", "src/WebShop/Shop.csproj" },
        new[] { "tools/Helper.csproj", "SERVER/Shop.csproj" },
        new[] { "Shop.sln", "src/Foo/Foo.csproj", "src/Foo.Core/Foo.Core.csproj" },
        new[] { "src/Foo/Foo.csproj", "tests/Foo.Tests/Foo.Tests.csproj" },
        new[] { "Foo.csproj", "bin/Web.csproj", "obj/Api.csproj" }
    };

    [Theory]
    [MemberData(nameof(ProjectTrees))]
    public void The_same_project_is_chosen_among_several(string[] files)
    {
        foreach (var file in files)
            Write(file, file.EndsWith(".csproj") ? Csproj("Microsoft.NET.Sdk.Web", "Npgsql") : "");

        var inspection = Inspect();
        var judged = inspection.RootDirectory is null ? Root : Path.Combine(Root, inspection.RootDirectory);
        var published = Buildpacks.Detect(judged, 8080)!.Value.Dockerfile;

        // The evidence names the project that was read, and the generated Dockerfile the one it publishes.
        var read = Service(inspection, DetectedServiceKind.Postgres).Evidence.Should().ContainSingle().Subject.File;
        published.Should().Contain($"dotnet publish \"{read}\"");
    }

    [Fact]
    public void A_buildpack_note_uses_the_buildpacks_own_words_for_django()
    {
        Write("requirements.txt");
        Write("manage.py");

        Inspect().Notes.Should().ContainSingle(n => n.Code == "django-unsupported")
            .Which.Message.Should().Be(Buildpacks.DetectionHint(Root));
    }

    [Fact]
    public void A_buildpack_note_uses_the_buildpacks_own_words_for_a_missing_entry_file()
    {
        Write("requirements.txt");

        Inspect().Notes.Should().ContainSingle(n => n.Code == "no-stack-recognised")
            .Which.Message.Should().Be(Buildpacks.DetectionHint(Root));
    }

    // --- ServiceCatalog ----------------------------------------------------------------------

    private static DetectedServiceKind KindOf(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql => DetectedServiceKind.Postgres,
        ManagedServiceType.MySql => DetectedServiceKind.MySql,
        ManagedServiceType.MariaDb => DetectedServiceKind.MariaDb,
        ManagedServiceType.Redis => DetectedServiceKind.Redis,
        ManagedServiceType.MongoDb => DetectedServiceKind.MongoDb,
        ManagedServiceType.RabbitMq => DetectedServiceKind.RabbitMq,
        ManagedServiceType.Nats => DetectedServiceKind.Nats,
        ManagedServiceType.Meilisearch => DetectedServiceKind.Meilisearch,
        _ => throw new InvalidOperationException($"A new service type, {type}: give it a DetectedServiceKind and an AttachEnvNames row.")
    };

    public static TheoryData<ManagedServiceType> EveryServiceType()
    {
        var data = new TheoryData<ManagedServiceType>();
        foreach (var type in Enum.GetValues<ManagedServiceType>()) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryServiceType))]
    public void The_attach_names_are_exactly_what_an_attachment_sets(ManagedServiceType type)
    {
        var real = ServiceCatalog.All[type].AttachEnv(new ServiceCreds("host", 1234, "user", "secret", "db")).Keys;

        AttachEnvNames.All[KindOf(type)].Should().BeEquivalentTo(real);
    }

    [Fact]
    public void Every_kind_the_inspector_can_detect_has_attach_names_and_a_real_service_type()
    {
        var mapped = Enum.GetValues<ManagedServiceType>().Select(KindOf).ToList();

        Enum.GetValues<DetectedServiceKind>().Should().BeEquivalentTo(mapped);
        AttachEnvNames.All.Keys.Should().BeEquivalentTo(mapped);
    }
}
