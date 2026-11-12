using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What <c>deploy/launch-proof.sh</c> takes for granted about its own fixtures, checked here in seconds.
/// The proof runs only on a throwaway host and takes half an hour, and every launch in it is
/// <c>harbora launch --yes</c>, which creates exactly the services the inspector is certain of. A fixture
/// whose database stops being found would still launch, with no database, and fail twenty minutes later
/// with a message about a connection. None of this says a launch works: only a real run says that.
/// </summary>
public class ProofFixtureTests
{
    private static readonly string Fixtures = Path.Combine(TestPaths.RepoRoot, "deploy", "proof-fixtures");

    public static TheoryData<string, DetectedStack, DetectedAppKind, int?, DetectedServiceKind[]> Launched => new()
    {
        { "node-postgres", DetectedStack.Node, DetectedAppKind.Web, 3000, [DetectedServiceKind.Postgres] },
        { "crashes", DetectedStack.Node, DetectedAppKind.Web, 3000, [] },
        { "wrong-database-host", DetectedStack.Node, DetectedAppKind.Web, 3000, [DetectedServiceKind.Postgres] },
        { "python-worker-redis", DetectedStack.Python, DetectedAppKind.Worker, null, [DetectedServiceKind.Redis] },
        { "two-apps/api", DetectedStack.Node, DetectedAppKind.Web, 3000, [] },
        { "vite-spa", DetectedStack.Frontend, DetectedAppKind.Web, 80, [] },
        { "cra-shaped", DetectedStack.Frontend, DetectedAppKind.Web, 80, [] },
        { "node-build-var", DetectedStack.Node, DetectedAppKind.Web, 3000, [] },
        { "python-marked-var", DetectedStack.Python, DetectedAppKind.Web, 8000, [] },
        { "php-pdo", DetectedStack.Php, DetectedAppKind.Web, 80, [DetectedServiceKind.MySql] },

        // The stacks and engines of round nine.
        { "dotnet-postgres", DetectedStack.DotNet, DetectedAppKind.Web, 8080, [DetectedServiceKind.Postgres] },
        { "go-nats", DetectedStack.Go, DetectedAppKind.Web, 8080, [DetectedServiceKind.Nats] },
        { "nextjs-ssr", DetectedStack.Node, DetectedAppKind.Web, 3000, [] },
        { "pnpm-mongo", DetectedStack.Node, DetectedAppKind.Web, 3000, [DetectedServiceKind.MongoDb] },
        { "yarn-mariadb", DetectedStack.Node, DetectedAppKind.Web, 3000, [DetectedServiceKind.MariaDb] },
        { "python-rabbitmq", DetectedStack.Python, DetectedAppKind.Web, 8000, [DetectedServiceKind.RabbitMq] },
        { "node-meilisearch", DetectedStack.Node, DetectedAppKind.Web, 3000, [DetectedServiceKind.Meilisearch] },

        // Pushed to a Git server the proof starts, and launched from there.
        { "git-node", DetectedStack.Node, DetectedAppKind.Web, 3000, [] },
        { "git-build", DetectedStack.Node, DetectedAppKind.Web, 3000, [] }
    };

    [Theory]
    [MemberData(nameof(Launched))]
    public void A_fixture_is_read_as_what_the_proof_launches_it_as(
        string folder, DetectedStack stack, DetectedAppKind kind, int? port, DetectedServiceKind[] certain)
    {
        var inspection = SourceInspector.Inspect(Path.Combine(Fixtures, folder));

        inspection.Stack.Should().Be(stack);
        inspection.Kind.Should().Be(kind);
        inspection.Port.Should().Be(port);
        inspection.RootDirectory.Should().BeNull("the proof launches the folder itself");
        inspection.RootCandidates.Should().BeEmpty("--yes cannot choose between folders");
        inspection.Services.Where(s => s.Certain).Select(s => s.Kind).Should().BeEquivalentTo(certain,
            "--yes creates the services the inspector is certain of, and no flag can add one");
    }

    [Theory]
    [InlineData("dotnet-postgres", ".NET", "mcr.microsoft.com/dotnet/aspnet:10.0", "ENTRYPOINT [\"dotnet\", \"Shop.Web.dll\"]")]
    [InlineData("go-nats", "Go", "golang:1.26-alpine", "go build -o /app/server .")]
    [InlineData("nextjs-ssr", "Node.js", "RUN npm install", "npm start")]
    [InlineData("pnpm-mongo", "Node.js", "corepack enable && pnpm install", "pnpm start")]
    [InlineData("yarn-mariadb", "Node.js", "corepack enable && yarn install", "yarn start")]
    [InlineData("python-rabbitmq", "Python", "pip install --no-cache-dir -r requirements.txt", "CMD [\"python\", \"app.py\"]")]
    [InlineData("node-meilisearch", "Node.js", "RUN npm install", "npm start")]
    public void The_image_a_fixture_gets_is_the_one_the_proof_means_to_exercise(
        string folder, string stack, string firstMark, string secondMark)
    {
        var inspection = SourceInspector.Inspect(Path.Combine(Fixtures, folder));

        var pack = Buildpacks.Detect(Path.Combine(Fixtures, folder), inspection.Port ?? 3000, ServiceKind.Web, inspection.StartCommand);

        pack.Should().NotBeNull();
        pack!.Value.Stack.Should().Be(stack);
        pack.Value.Dockerfile.Should().Contain(firstMark).And.Contain(secondMark);
    }

    [Fact]
    public void The_dotnet_fixture_keeps_its_driver_in_a_project_the_web_one_references()
    {
        // What makes it worth launching: the database is found by following a ProjectReference, and the
        // project that is published is not at the top of the tree.
        var root = Path.Combine(Fixtures, "dotnet-postgres");

        File.ReadAllText(Path.Combine(root, "src", "Shop.Web", "Shop.Web.csproj")).Should().NotContain("Npgsql");
        File.ReadAllText(Path.Combine(root, "src", "Shop.Data", "Shop.Data.csproj")).Should().Contain("Npgsql");
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public void Every_fixture_folder_is_one_this_knows_about()
    {
        // A fixture added without a row above is one whose launch can break with nothing here to say so.
        // These three are launched to fail, or with a folder chosen by hand, and are asserted in the proof.
        string[] notLaunchedWhole = ["two-apps", "frontend-build-fails", "frontend-no-index"];

        var known = Launched.Select(row => ((string)row[0]).Split('/')[0]).Concat(notLaunchedWhole);

        Directory.EnumerateDirectories(Fixtures).Select(Path.GetFileName).Should().BeEquivalentTo(known.Distinct());
    }
}
