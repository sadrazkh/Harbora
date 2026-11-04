using FluentAssertions;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>Which managed services a source tree appears to need, and the evidence shown beside each.</summary>
public class SourceInspectorServiceTests : InspectorTreeTests
{
    // --- The table, row by row ---------------------------------------------------------------

    [Theory]
    [InlineData("pg", DetectedServiceKind.Postgres)]
    [InlineData("postgres", DetectedServiceKind.Postgres)]
    [InlineData("pg-promise", DetectedServiceKind.Postgres)]
    [InlineData("mysql", DetectedServiceKind.MySql)]
    [InlineData("mysql2", DetectedServiceKind.MySql)]
    [InlineData("mariadb", DetectedServiceKind.MariaDb)]
    [InlineData("redis", DetectedServiceKind.Redis)]
    [InlineData("ioredis", DetectedServiceKind.Redis)]
    [InlineData("bull", DetectedServiceKind.Redis)]
    [InlineData("bullmq", DetectedServiceKind.Redis)]
    [InlineData("mongoose", DetectedServiceKind.MongoDb)]
    [InlineData("mongodb", DetectedServiceKind.MongoDb)]
    [InlineData("amqplib", DetectedServiceKind.RabbitMq)]
    [InlineData("amqp-connection-manager", DetectedServiceKind.RabbitMq)]
    [InlineData("nats", DetectedServiceKind.Nats)]
    [InlineData("meilisearch", DetectedServiceKind.Meilisearch)]
    public void Node(string library, DetectedServiceKind kind)
    {
        Write("package.json", PackageJson(library));

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("package.json", $"\"{library}\" in dependencies"));
    }

    [Theory]
    [InlineData("psycopg2", DetectedServiceKind.Postgres)]
    [InlineData("psycopg2-binary", DetectedServiceKind.Postgres)]
    [InlineData("psycopg", DetectedServiceKind.Postgres)]
    [InlineData("asyncpg", DetectedServiceKind.Postgres)]
    [InlineData("mysqlclient", DetectedServiceKind.MySql)]
    [InlineData("pymysql", DetectedServiceKind.MySql)]
    [InlineData("aiomysql", DetectedServiceKind.MySql)]
    [InlineData("mysql-connector-python", DetectedServiceKind.MySql)]
    [InlineData("mariadb", DetectedServiceKind.MariaDb)]
    [InlineData("redis", DetectedServiceKind.Redis)]
    [InlineData("aioredis", DetectedServiceKind.Redis)]
    [InlineData("rq", DetectedServiceKind.Redis)]
    [InlineData("pymongo", DetectedServiceKind.MongoDb)]
    [InlineData("motor", DetectedServiceKind.MongoDb)]
    [InlineData("mongoengine", DetectedServiceKind.MongoDb)]
    [InlineData("beanie", DetectedServiceKind.MongoDb)]
    [InlineData("pika", DetectedServiceKind.RabbitMq)]
    [InlineData("aio-pika", DetectedServiceKind.RabbitMq)]
    [InlineData("nats-py", DetectedServiceKind.Nats)]
    [InlineData("meilisearch", DetectedServiceKind.Meilisearch)]
    public void Python(string library, DetectedServiceKind kind)
    {
        Write("requirements.txt", library + "==1.2.3");
        Write("main.py");

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("requirements.txt", $"\"{library}\" in requirements.txt"));
    }

    [Theory]
    [InlineData("Npgsql", DetectedServiceKind.Postgres)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", DetectedServiceKind.Postgres)]
    [InlineData("MySqlConnector", DetectedServiceKind.MySql)]
    [InlineData("MySql.Data", DetectedServiceKind.MySql)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", DetectedServiceKind.MySql)]
    [InlineData("StackExchange.Redis", DetectedServiceKind.Redis)]
    [InlineData("MongoDB.Driver", DetectedServiceKind.MongoDb)]
    [InlineData("RabbitMQ.Client", DetectedServiceKind.RabbitMq)]
    [InlineData("MassTransit.RabbitMQ", DetectedServiceKind.RabbitMq)]
    [InlineData("NATS.Client", DetectedServiceKind.Nats)]
    [InlineData("NATS.Net", DetectedServiceKind.Nats)]
    [InlineData("Meilisearch", DetectedServiceKind.Meilisearch)]
    public void DotNet(string package, DetectedServiceKind kind)
    {
        Write("App.csproj", Csproj("Microsoft.NET.Sdk.Web", package));

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("App.csproj", $"\"{package}\" in PackageReference"));
    }

    [Theory]
    [InlineData("github.com/lib/pq", DetectedServiceKind.Postgres)]
    [InlineData("github.com/jackc/pgx/v5", DetectedServiceKind.Postgres)]
    [InlineData("github.com/go-sql-driver/mysql", DetectedServiceKind.MySql)]
    [InlineData("github.com/redis/go-redis/v9", DetectedServiceKind.Redis)]
    [InlineData("github.com/gomodule/redigo", DetectedServiceKind.Redis)]
    [InlineData("go.mongodb.org/mongo-driver", DetectedServiceKind.MongoDb)]
    [InlineData("go.mongodb.org/mongo-driver/v2", DetectedServiceKind.MongoDb)]
    [InlineData("github.com/rabbitmq/amqp091-go", DetectedServiceKind.RabbitMq)]
    [InlineData("github.com/streadway/amqp", DetectedServiceKind.RabbitMq)]
    [InlineData("github.com/nats-io/nats.go", DetectedServiceKind.Nats)]
    [InlineData("github.com/meilisearch/meilisearch-go", DetectedServiceKind.Meilisearch)]
    public void Go(string module, DetectedServiceKind kind)
    {
        Write("go.mod", $"module x\n\ngo 1.22\n\nrequire (\n\t{module} v1.0.0\n)\n");

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("go.mod", $"\"{module}\" in go.mod"));
    }

    [Theory]
    [InlineData("ext-pgsql", DetectedServiceKind.Postgres)]
    [InlineData("ext-mysqli", DetectedServiceKind.MySql)]
    [InlineData("ext-pdo_mysql", DetectedServiceKind.MySql)]
    [InlineData("predis/predis", DetectedServiceKind.Redis)]
    [InlineData("mongodb/mongodb", DetectedServiceKind.MongoDb)]
    [InlineData("php-amqplib/php-amqplib", DetectedServiceKind.RabbitMq)]
    [InlineData("meilisearch/meilisearch-php", DetectedServiceKind.Meilisearch)]
    public void Php(string package, DetectedServiceKind kind)
    {
        Write("composer.json", "{\"require\":{\"" + package + "\":\"*\"}}");

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("composer.json", $"\"{package}\" in require"));
    }

    // --- Reading each language's file carefully ----------------------------------------------

    [Fact]
    public void A_driver_only_a_developer_installs_is_shown_but_not_ticked()
    {
        Write("package.json", "{\"devDependencies\":{\"pg\":\"8\"}}");

        var service = Service(Inspect(), DetectedServiceKind.Postgres);

        service.Certain.Should().BeFalse("creating a database costs money, and a test helper is not consent");
        service.Evidence.Should().Equal(new ServiceEvidence("package.json", "\"pg\" in devDependencies"));
    }

    [Fact]
    public void A_driver_in_both_sections_is_certain_and_shows_both()
    {
        Write("package.json", "{\"dependencies\":{\"pg\":\"8\"},\"devDependencies\":{\"pg\":\"8\"}}");

        var service = Service(Inspect(), DetectedServiceKind.Postgres);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().HaveCount(2);
    }

    [Fact]
    public void Composer_require_dev_is_weak_in_the_same_way()
    {
        Write("composer.json", "{\"require-dev\":{\"predis/predis\":\"*\"}}");

        Service(Inspect(), DetectedServiceKind.Redis).Certain.Should().BeFalse();
    }

    [Fact]
    public void Node_modules_holding_a_decoy_is_never_read()
    {
        Write("package.json", PackageJson("express"));
        Write("node_modules/pg/package.json", PackageJson("pg"));
        Write("node_modules/.env.example", "MONGODB_URI=mongodb://decoy");
        Write("node_modules/decoy/requirements.txt", "psycopg2");

        Inspect().Services.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PyMySQL==1.1", DetectedServiceKind.MySql)]
    [InlineData("aio_pika>=9", DetectedServiceKind.RabbitMq)]
    [InlineData("psycopg[binary]>=3", DetectedServiceKind.Postgres)]
    [InlineData("redis ; python_version > '3.8'", DetectedServiceKind.Redis)]
    [InlineData("  motor~=3  # the async one", DetectedServiceKind.MongoDb)]
    [InlineData("pymongo @ https://example.com/p.zip", DetectedServiceKind.MongoDb)]
    [InlineData("git+https://example.com/pika.git#egg=pika", DetectedServiceKind.RabbitMq)]
    [InlineData("-e git+https://example.com/asyncpg.git@main#egg=asyncpg", DetectedServiceKind.Postgres)]
    public void Requirements_are_read_however_they_are_spelled(string line, DetectedServiceKind kind)
    {
        Write("requirements.txt", $"# comment\n\n--index-url https://pypi.example.com\n{line}\n");
        Write("main.py");

        Inspect().Services.Select(s => s.Kind).Should().Contain(kind);
    }

    [Theory]
    [InlineData("# psycopg2")]
    [InlineData("--find-links https://example.com/redis")]
    [InlineData("https://example.com/redis.zip")]
    public void Lines_that_only_mention_a_library_do_not_count(string line)
    {
        Write("requirements.txt", line);
        Write("main.py");

        Inspect().Services.Should().BeEmpty();
    }

    [Fact]
    public void Requirements_follow_an_include_inside_the_tree()
    {
        Write("requirements.txt", "-r requirements/base.txt\n");
        Write("requirements/base.txt", "psycopg2\n-r ../requirements.txt\n");
        Write("main.py");

        Service(Inspect(), DetectedServiceKind.Postgres).Evidence.Should()
            .Equal(new ServiceEvidence("requirements/base.txt", "\"psycopg2\" in base.txt"));
    }

    [Fact]
    public void Requirements_do_not_follow_an_include_out_of_the_tree()
    {
        Write("app/requirements.txt", "-r ../outside.txt\n");
        Write("app/main.py");
        Write("outside.txt", "psycopg2\n");

        SourceInspector.Inspect(Path.Combine(Root, "app")).Services.Should().BeEmpty();
    }

    [Fact]
    public void Pyproject_project_dependencies_are_read_including_a_bracket_inside_a_string()
    {
        Write("pyproject.toml", """
            [build-system]
            requires = ["redis-not-a-dep"]

            [project]
            name = "x"
            dependencies = [
                "uvicorn[standard]>=0.30",  # the bracket above must not end this array
                'asyncpg>=0.29',
                "aiogram",
            ]
            optional-dependencies = { dev = ["pymongo"] }

            [tool.other]
            dependencies = ["pika"]
            """);
        Write("main.py");

        var result = Inspect();

        result.Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres);
        result.Kind.Should().Be(DetectedAppKind.Web, "uvicorn was read too");
    }

    [Fact]
    public void Pyproject_on_one_line_is_read()
    {
        Write("pyproject.toml", "[project]\ndependencies = [\"redis\", \"pika\"]\n");
        Write("main.py");

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Redis, DetectedServiceKind.RabbitMq);
    }

    [Fact]
    public void Poetry_dependencies_are_read_and_its_dev_group_is_not()
    {
        Write("pyproject.toml", """
            [tool.poetry.dependencies]
            python = "^3.12"
            psycopg2-binary = "^2.9"

            [tool.poetry.group.dev.dependencies]
            pymongo = "^4"
            """);
        Write("main.py");

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres);
    }

    [Fact]
    public void A_Pipfile_has_its_packages_read_and_its_dev_packages_not()
    {
        Write("Pipfile", """
            [[source]]
            url = "https://pypi.org/simple"

            [packages]
            redis = "*"

            [dev-packages]
            pymongo = "*"
            """);
        Write("main.py");

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Redis);
    }

    [Fact]
    public void A_go_mod_counts_what_the_code_imports_and_nothing_else()
    {
        Write("go.mod", """
            module github.com/acme/lib/pq

            require github.com/lib/pq v1.0.0

            require (
                github.com/redis/go-redis/v9 v9.0.0 // indirect
                github.com/not-gomodule/redigo v1.0.0
                // github.com/streadway/amqp v1.0.0
            )

            replace github.com/nats-io/nats.go => ../nats
            """);

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres);
    }

    [Fact]
    public void A_project_reference_is_followed_since_the_driver_usually_lives_in_a_data_project()
    {
        Write("Shop.sln");
        Write("Shop.Web/Shop.Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><ProjectReference Include=\"..\\Shop.Data\\Shop.Data.csproj\" /></ItemGroup></Project>");
        Write("Shop.Data/Shop.Data.csproj", Csproj("Microsoft.NET.Sdk", "Npgsql.EntityFrameworkCore.PostgreSQL"));

        var service = Service(Inspect(), DetectedServiceKind.Postgres);

        service.Evidence.Should().Equal(new ServiceEvidence("Shop.Data/Shop.Data.csproj", "\"Npgsql.EntityFrameworkCore.PostgreSQL\" in PackageReference"));
    }

    [Fact]
    public void A_project_the_app_does_not_reference_says_nothing_about_it()
    {
        Write("Shop.sln");
        Write("Shop.Web/Shop.Web.csproj", Csproj());
        Write("tests/Shop.Tests/Shop.Tests.csproj",
            Csproj("Microsoft.NET.Sdk", "Microsoft.EntityFrameworkCore.Sqlite", "Npgsql"));

        var result = Inspect();

        result.Services.Should().BeEmpty();
        Codes(result).Should().NotContain("sqlite-needs-volume");
    }

    [Fact]
    public void A_reference_cycle_ends()
    {
        Write("Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../Web/Web.csproj\" /><PackageReference Include=\"Npgsql\" /></ItemGroup></Project>");
        Write("Shop.sln");

        Service(Inspect(), DetectedServiceKind.Postgres).Certain.Should().BeTrue();
    }

    [Fact]
    public void A_reference_to_a_project_that_is_not_there_is_the_builds_problem()
    {
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><ProjectReference Include=\"..\\gone\\Gone.csproj\" /><ProjectReference Include=\"nope\\Nope.csproj\" /></ItemGroup></Project>");

        Codes(Inspect()).Should().NotContain("manifest-unreadable");
    }

    [Fact]
    public void A_stack_of_another_language_does_not_have_its_nested_projects_read()
    {
        Write("package.json", "{}");
        Write("tools/Gen/Gen.csproj", Csproj("Microsoft.NET.Sdk", "Npgsql"));

        Inspect().Services.Should().BeEmpty();
    }

    [Fact]
    public void A_laravel_app_that_builds_as_node_still_needs_its_database()
    {
        Write("package.json", "{}");
        Write("composer.json", "{\"require\":{\"ext-pgsql\":\"*\"}}");

        Service(Inspect(), DetectedServiceKind.Postgres).Certain.Should().BeTrue();
    }

    // --- Prisma, Laravel, URLs, compose ------------------------------------------------------

    [Theory]
    [InlineData("postgresql", DetectedServiceKind.Postgres)]
    [InlineData("postgres", DetectedServiceKind.Postgres)]
    [InlineData("mysql", DetectedServiceKind.MySql)]
    [InlineData("mongodb", DetectedServiceKind.MongoDb)]
    public void Prisma_provider_is_certain(string provider, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write("prisma/schema.prisma", $$"""
            generator client {
              provider = "prisma-client-js"
            }

            datasource db {
              provider = "{{provider}}"
              url      = env("DATABASE_URL")
            }
            """);

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("prisma/schema.prisma", $"datasource provider = \"{provider}\""));
    }

    [Fact]
    public void A_commented_out_provider_and_the_generators_provider_are_not_the_engine()
    {
        Write("package.json", "{}");
        Write("prisma/schema.prisma", """
            generator mysql {
              provider = "mysql"
            }
            datasource db {
              // provider = "postgresql"
              provider = "mongodb"
            }
            """);

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.MongoDb);
    }

    [Theory]
    [InlineData("pgsql", DetectedServiceKind.Postgres)]
    [InlineData("mysql", DetectedServiceKind.MySql)]
    [InlineData("mariadb", DetectedServiceKind.MariaDb)]
    public void Laravels_db_connection_is_certain(string connection, DetectedServiceKind kind)
    {
        Write("composer.json", "{}");
        Write(".env.example", $"DB_CONNECTION={connection}\n");

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence(".env.example", $"DB_CONNECTION={connection}"));
    }

    [Theory]
    [InlineData("postgres://u:hunter2@db/x", DetectedServiceKind.Postgres)]
    [InlineData("postgresql://u:hunter2@db/x", DetectedServiceKind.Postgres)]
    [InlineData("mysql://u:hunter2@db/x", DetectedServiceKind.MySql)]
    [InlineData("mongodb://u:hunter2@db/x", DetectedServiceKind.MongoDb)]
    [InlineData("mongodb+srv://u:hunter2@db/x", DetectedServiceKind.MongoDb)]
    public void A_database_url_says_which_engine_by_its_scheme(string value, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write(".env.example", $"DATABASE_URL={value}\n");

        var service = Service(Inspect(), kind);

        service.Certain.Should().BeTrue();
        service.Evidence.Should().ContainSingle()
            .Which.Reason.Should().NotContain("hunter2", "evidence is shown on a page, and an example's URL may hold a real password");
    }

    [Fact]
    public void A_mysql_url_on_a_project_known_to_use_mariadb_has_not_chosen_a_second_engine()
    {
        Write("package.json", PackageJson("mariadb"));
        Write(".env.example", "DATABASE_URL=mysql://u:p@db/x\n");

        Inspect().Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.MariaDb);
    }

    [Theory]
    [InlineData("REDIS_URL", DetectedServiceKind.Redis)]
    [InlineData("AMQP_URL", DetectedServiceKind.RabbitMq)]
    [InlineData("NATS_URL", DetectedServiceKind.Nats)]
    public void The_named_urls_are_certain_with_a_value_and_without_one(string name, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write(".env.example", $"{name}=\n");

        Service(Inspect(), kind).Certain.Should().BeTrue();

        Write(".env.example", name switch
        {
            "REDIS_URL" => "REDIS_URL=redis://cache:6379",
            "AMQP_URL" => "AMQP_URL=amqp://mq:5672",
            _ => "NATS_URL=nats://bus:4222"
        });

        Service(Inspect(), kind).Certain.Should().BeTrue();
    }

    [Theory]
    [InlineData("REDIS_HOST=", DetectedServiceKind.Redis)]
    [InlineData("REDIS_HOST=cache", DetectedServiceKind.Redis)]
    [InlineData("PGHOST=", DetectedServiceKind.Postgres)]
    [InlineData("POSTGRES_PASSWORD=", DetectedServiceKind.Postgres)]
    [InlineData("MYSQL_HOST=", DetectedServiceKind.MySql)]
    [InlineData("MONGO_URI=", DetectedServiceKind.MongoDb)]
    [InlineData("RABBITMQ_HOST=", DetectedServiceKind.RabbitMq)]
    [InlineData("NATS_HOST=", DetectedServiceKind.Nats)]
    [InlineData("MEILI_MASTER_KEY=", DetectedServiceKind.Meilisearch)]
    [InlineData("MEILISEARCH_HOST=http://search:7700", DetectedServiceKind.Meilisearch)]
    public void A_bare_name_that_implies_a_kind_is_only_a_guess(string line, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write(".env.example", line + "\n");

        Service(Inspect(), kind).Certain.Should().BeFalse();
    }

    [Theory]
    [InlineData("MONGO_URI=mongodb://db/x", DetectedServiceKind.MongoDb)]
    [InlineData("POSTGRES_URL=postgres://db/x", DetectedServiceKind.Postgres)]
    [InlineData("CACHE_URL=redis://cache", DetectedServiceKind.Redis)]
    [InlineData("RABBITMQ_URL=amqp://mq", DetectedServiceKind.RabbitMq)]
    [InlineData("DB_URL=postgres://db/x", DetectedServiceKind.Postgres)]
    public void A_name_whose_value_confirms_it_is_certain(string line, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write(".env.example", line + "\n");

        Service(Inspect(), kind).Certain.Should().BeTrue();
    }

    [Theory]
    [InlineData("CACHE_URL=memcached://cache:11211")]
    [InlineData("REDIS_URL=https://upstash.example.com")]
    [InlineData("DATABASE_URL=https://db.example.com")]
    [InlineData("LOG_LEVEL=info")]
    [InlineData("DB_HOST=db")]
    public void A_name_and_value_that_do_not_say_a_kind_detect_nothing(string line)
    {
        Write("package.json", "{}");
        Write(".env.example", line + "\n");

        Inspect().Services.Should().BeEmpty();
    }

    [Theory]
    [InlineData("postgres:16-alpine", DetectedServiceKind.Postgres)]
    [InlineData("postgres", DetectedServiceKind.Postgres)]
    [InlineData("\"postgres:16\"", DetectedServiceKind.Postgres)]
    [InlineData("'postgres:16'  # the database", DetectedServiceKind.Postgres)]
    [InlineData("docker.io/library/postgres:16", DetectedServiceKind.Postgres)]
    [InlineData("postgres@sha256:abc", DetectedServiceKind.Postgres)]
    [InlineData("${DB_IMAGE:-postgres:16}", DetectedServiceKind.Postgres)]
    [InlineData("mysql:8", DetectedServiceKind.MySql)]
    [InlineData("mariadb:11", DetectedServiceKind.MariaDb)]
    [InlineData("mongo:7", DetectedServiceKind.MongoDb)]
    [InlineData("redis:7-alpine", DetectedServiceKind.Redis)]
    [InlineData("rabbitmq:3-management", DetectedServiceKind.RabbitMq)]
    [InlineData("nats:2.10", DetectedServiceKind.Nats)]
    [InlineData("getmeili/meilisearch:v1.5", DetectedServiceKind.Meilisearch)]
    public void A_compose_image_beside_a_build_is_certain(string image, DetectedServiceKind kind)
    {
        Write("package.json", "{}");
        Write("docker-compose.yml", $"services:\n  svc:\n    image: {image}\n");

        Service(Inspect(), kind).Certain.Should().BeTrue();
    }

    [Theory]
    [InlineData("myorg/postgres:16")]
    [InlineData("postgres-exporter:1")]
    [InlineData("registry.example.com:5000/postgres")]
    [InlineData("nginx:alpine")]
    public void Any_other_image_is_not_that_service(string image)
    {
        Write("package.json", "{}");
        Write("docker-compose.yml", $"services:\n  svc:\n    image: {image}\n");

        Inspect().Services.Should().BeEmpty();
    }

    [Fact]
    public void A_Dockerfile_plus_a_dev_compose_file_with_postgres_builds_the_Dockerfile_and_wants_postgres()
    {
        Write("Dockerfile", "FROM node:20\nEXPOSE 3000\n");
        Write("docker-compose.dev.yml", "services:\n  db:\n    image: postgres:16\n  app:\n    build: .\n");

        var result = Inspect();

        result.Stack.Should().Be(DetectedStack.Dockerfile);
        result.Port.Should().Be(3000);
        var service = Service(result, DetectedServiceKind.Postgres);
        service.Certain.Should().BeTrue();
        service.Evidence.Should().Equal(new ServiceEvidence("docker-compose.dev.yml", "image: postgres:16"));
        Codes(result).Should().NotContain("compose-runs-own-services");
    }

    [Fact]
    public void Evidence_piles_up_on_one_row_per_kind_in_a_stable_order()
    {
        Write("package.json", PackageJson("redis", "pg"));
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres\n");
        Write(".env.example", "DATABASE_URL=postgres://db/x\n");

        var result = Inspect();

        result.Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres, DetectedServiceKind.Redis);
        Service(result, DetectedServiceKind.Postgres).Evidence.Select(e => e.File)
            .Should().Equal("package.json", "docker-compose.yml", ".env.example");
    }

    [Fact]
    public void Paths_in_evidence_are_relative_to_the_app_rather_than_to_the_zip_it_arrived_in()
    {
        Write("repo-main/package.json", PackageJson("pg"));

        var result = Inspect();

        result.RootDirectory.Should().Be("repo-main");
        Service(result, DetectedServiceKind.Postgres).Evidence.Single().File.Should().Be("package.json");
    }

    // --- Notes -------------------------------------------------------------------------------

    [Theory]
    [InlineData("package.json", "{\"dependencies\":{\"sqlite3\":\"5\"}}")]
    [InlineData("package.json", "{\"dependencies\":{\"better-sqlite3\":\"9\"}}")]
    [InlineData("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" /></ItemGroup></Project>")]
    [InlineData(".env.example", "DB_CONNECTION=sqlite")]
    [InlineData(".env.example", "DATABASE_URL=sqlite:///./app.db")]
    [InlineData("prisma/schema.prisma", "datasource db {\n  provider = \"sqlite\"\n}\n")]
    public void Sqlite_is_warned_about(string file, string content)
    {
        Write("index.html");
        Write(file, content);

        var result = Inspect();

        Codes(result).Should().Contain("sqlite-needs-volume");
        result.Services.Should().BeEmpty();
    }

    [Theory]
    [InlineData("package.json", "{\"dependencies\":{\"mssql\":\"10\"}}")]
    [InlineData("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" /></ItemGroup></Project>")]
    [InlineData("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Microsoft.Data.SqlClient\" /></ItemGroup></Project>")]
    [InlineData(".env.example", "DB_CONNECTION=sqlsrv")]
    [InlineData("prisma/schema.prisma", "datasource db {\n  provider = \"sqlserver\"\n}\n")]
    public void Sql_server_is_called_unsupported(string file, string content)
    {
        Write("index.html");
        Write(file, content);

        var result = Inspect();

        Codes(result).Should().Contain("sql-server-unsupported");
        result.Services.Should().BeEmpty();
    }

    [Fact]
    public void A_sqlite_library_only_a_developer_installs_says_nothing_about_the_running_app()
    {
        Write("package.json", "{\"devDependencies\":{\"better-sqlite3\":\"9\",\"mssql\":\"10\"}}");

        Codes(Inspect()).Should().NotContain(["sqlite-needs-volume", "sql-server-unsupported"]);
    }
}
