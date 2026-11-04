using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>Reading the example env files and saying what each name means, rule by rule, in the brief's order.</summary>
public class SourceInspectorEnvTests : InspectorTreeTests
{
    private SourceInspection InspectExample(string example, params string[] libraries)
    {
        Write("package.json", PackageJson(libraries));
        Write(".env.example", example);
        return Inspect();
    }

    // --- Which files, and how they are parsed ------------------------------------------------

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    [InlineData(".env.dist")]
    [InlineData("env.example")]
    [InlineData("example.env")]
    [InlineData("sample.env")]
    public void Each_example_file_name_is_read(string file)
    {
        Write("package.json", "{}");
        Write(file, "LOG_LEVEL=info");

        Var(Inspect(), "LOG_LEVEL").SourceFile.Should().Be(file);
    }

    [Fact]
    public void The_first_file_found_wins_per_name_and_later_files_still_add_names()
    {
        Write("package.json", "{}");
        Write("sample.env", "A=from-sample\nC=from-sample\n");
        Write(".env.sample", "A=from-dot-sample\nB=from-dot-sample\n");
        Write(".env.example", "A=from-example\n");

        var result = Inspect();

        Var(result, "A").ExampleValue.Should().Be("from-example");
        Var(result, "A").SourceFile.Should().Be(".env.example");
        Var(result, "B").ExampleValue.Should().Be("from-dot-sample");
        Var(result, "C").ExampleValue.Should().Be("from-sample");
        result.EnvVars.Select(v => v.Name).Should().Equal("A", "B", "C");
    }

    [Theory]
    [InlineData("FOO=bar", "bar")]
    [InlineData("export FOO=bar", "bar")]
    [InlineData("  export   FOO=bar  ", "bar")]
    [InlineData("FOO = bar", "bar")]
    [InlineData("FOO=\"bar baz\"", "bar baz")]
    [InlineData("FOO='bar baz'", "bar baz")]
    [InlineData("FOO=\"bar\" # why", "bar")]
    [InlineData("FOO='bar' # why", "bar")]
    [InlineData("FOO=bar # why", "bar")]
    [InlineData("FOO=bar#baz", "bar#baz")]
    [InlineData("FOO=\"has # inside\"", "has # inside")]
    [InlineData("FOO=\"say \\\"hi\\\"\"", "say \\\"hi\\\"")]
    [InlineData("FOO=a=b=c", "a=b=c")]
    public void Lines_are_parsed_as_dotenv_has_them(string line, string expected)
    {
        Var(InspectExample($"# a comment\n\n{line}\n"), "FOO").ExampleValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("# FOO=bar")]
    [InlineData("   # FOO=bar")]
    [InlineData("no equals sign")]
    [InlineData("1BAD=name")]
    [InlineData("BAD-NAME=x")]
    [InlineData("=x")]
    public void Lines_that_are_not_assignments_are_nothing(string line)
    {
        InspectExample(line).EnvVars.Should().BeEmpty();
    }

    [Theory]
    [InlineData("FOO=")]
    [InlineData("FOO=   ")]
    [InlineData("FOO=  # only a comment")]
    public void A_name_with_no_value_has_no_example_value_and_needs_an_answer(string line)
    {
        var variable = Var(InspectExample(line), "FOO");

        variable.ExampleValue.Should().BeNull();
        variable.Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
    }

    [Fact]
    public void A_quoted_value_over_several_lines_is_one_value_and_its_lines_are_not_variables()
    {
        var result = InspectExample("KEY_FILE=\"-----BEGIN\nabc=\nA=1\n-----END\"\nAFTER=1\n");

        result.EnvVars.Select(v => v.Name).Should().Equal("KEY_FILE", "AFTER");
    }

    [Fact]
    public void An_unclosed_quote_does_not_lose_the_value()
    {
        Var(InspectExample("FOO=\"never closed"), "FOO").ExampleValue.Should().Contain("never closed");
    }

    [Fact]
    public void Windows_line_endings_are_not_part_of_a_value()
    {
        Var(InspectExample("FOO=bar\r\nBAZ=qux\r\n"), "FOO").ExampleValue.Should().Be("bar");
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16")]
    public void A_file_with_a_byte_order_mark_is_read(string encoding)
    {
        Write("package.json", "{}");
        Encoding chosen = encoding == "utf-16" ? Encoding.Unicode : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(Path.Combine(Root, ".env.example"), "FOO=bar\n", chosen);

        Var(Inspect(), "FOO").ExampleValue.Should().Be("bar");
    }

    // --- A real .env -------------------------------------------------------------------------

    [Fact]
    public void A_real_env_file_is_never_read_and_its_value_appears_nowhere()
    {
        const string secret = "sk_live_THIS_MUST_NOT_ESCAPE";
        Write("package.json", PackageJson("pg"));
        Write(".env", $"STRIPE_SECRET_KEY={secret}\nDATABASE_URL=mysql://root:{secret}@db/x\nONLY_IN_DOTENV=1\n");
        Write(".env.example", "STRIPE_SECRET_KEY=\n");

        var result = Inspect();

        JsonSerializer.Serialize(result).Should().NotContain(secret).And.NotContain("ONLY_IN_DOTENV");
        result.Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres);
        Var(result, "STRIPE_SECRET_KEY").ExampleValue.Should().BeNull();
        Codes(result).Should().Contain("env-file-ignored");
    }

    [Fact]
    public void A_real_env_file_in_the_folder_the_app_was_found_in_is_noted_too()
    {
        Write("repo-main/package.json", "{}");
        Write("repo-main/.env", "A=1");

        Codes(Inspect()).Should().Contain("env-file-ignored");
    }

    [Fact]
    public void No_env_file_no_note()
    {
        Codes(InspectExample("A=1")).Should().NotContain("env-file-ignored");
    }

    [Theory]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("production.env")]
    public void Other_files_with_real_values_are_not_read_either(string file)
    {
        Write("package.json", "{}");
        Write(file, "SOMETHING=real");

        Inspect().EnvVars.Should().BeEmpty();
    }

    // --- Rule 1: the platform's own ----------------------------------------------------------

    [Theory]
    [InlineData("PORT")]
    [InlineData("NODE_ENV")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("PYTHONUNBUFFERED")]
    public void The_platforms_own_names_are_dropped_entirely(string name)
    {
        InspectExample($"{name}=3000").EnvVars.Should().BeEmpty();
    }

    // --- Rule 2: what an attachment sets -----------------------------------------------------

    public static TheoryData<string, DetectedServiceKind> AttachNames()
    {
        var data = new TheoryData<string, DetectedServiceKind>();
        foreach (var (kind, names) in AttachEnvNames.All)
        foreach (var name in names)
            data.Add(name, kind);
        return data;
    }

    private static string LibraryFor(DetectedServiceKind kind) => kind switch
    {
        DetectedServiceKind.Postgres => "pg",
        DetectedServiceKind.MySql => "mysql2",
        DetectedServiceKind.MariaDb => "mariadb",
        DetectedServiceKind.Redis => "ioredis",
        DetectedServiceKind.MongoDb => "mongoose",
        DetectedServiceKind.RabbitMq => "amqplib",
        DetectedServiceKind.Nats => "nats",
        DetectedServiceKind.Meilisearch => "meilisearch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [MemberData(nameof(AttachNames))]
    public void Every_attach_name_of_a_detected_service_is_provided(string name, DetectedServiceKind kind)
    {
        var variable = Var(InspectExample($"{name}=changeme", LibraryFor(kind)), name);

        variable.Disposition.Should().Be(EnvVarDisposition.ProvidedByAttach);
        variable.Service.Should().Be(kind);
        variable.Role.Should().BeNull();
    }

    [Fact]
    public void Two_engines_both_setting_a_name_go_by_the_values_scheme_and_say_it_is_unclear()
    {
        var result = InspectExample("DATABASE_URL=mysql://db/x\n", "pg");

        result.Services.Select(s => s.Kind).Should().Equal(DetectedServiceKind.Postgres, DetectedServiceKind.MySql);
        Var(result, "DATABASE_URL").Service.Should().Be(DetectedServiceKind.MySql);
        Codes(result).Should().Contain("database-unclear");
    }

    [Fact]
    public void A_certain_engine_owns_a_shared_name_ahead_of_a_guessed_one()
    {
        var result = InspectExample("DATABASE_URL=\nMYSQL_HOST=\n", "pg");

        Var(result, "DATABASE_URL").Service.Should().Be(DetectedServiceKind.Postgres);
    }

    // --- Rule 3: other spellings -------------------------------------------------------------

    [Theory]
    [InlineData("MONGO_URI", DetectedServiceKind.MongoDb, EnvAliasRole.Url)]
    [InlineData("MONGO_URL", DetectedServiceKind.MongoDb, EnvAliasRole.Url)]
    [InlineData("MONGODB_URL", DetectedServiceKind.MongoDb, EnvAliasRole.Url)]
    [InlineData("POSTGRES_URL", DetectedServiceKind.Postgres, EnvAliasRole.Url)]
    [InlineData("POSTGRESQL_URL", DetectedServiceKind.Postgres, EnvAliasRole.Url)]
    [InlineData("PG_URL", DetectedServiceKind.Postgres, EnvAliasRole.Url)]
    [InlineData("POSTGRES_HOST", DetectedServiceKind.Postgres, EnvAliasRole.Host)]
    [InlineData("POSTGRES_USER", DetectedServiceKind.Postgres, EnvAliasRole.User)]
    [InlineData("POSTGRES_PASSWORD", DetectedServiceKind.Postgres, EnvAliasRole.Password)]
    [InlineData("POSTGRES_DB", DetectedServiceKind.Postgres, EnvAliasRole.Database)]
    [InlineData("REDIS_URI", DetectedServiceKind.Redis, EnvAliasRole.Url)]
    [InlineData("REDIS_CONNECTION", DetectedServiceKind.Redis, EnvAliasRole.Url)]
    [InlineData("CACHE_URL", DetectedServiceKind.Redis, EnvAliasRole.Url)]
    [InlineData("RABBITMQ_URI", DetectedServiceKind.RabbitMq, EnvAliasRole.Url)]
    [InlineData("CLOUDAMQP_URL", DetectedServiceKind.RabbitMq, EnvAliasRole.Url)]
    [InlineData("NATS_SERVER", DetectedServiceKind.Nats, EnvAliasRole.Url)]
    [InlineData("NATS_SERVERS", DetectedServiceKind.Nats, EnvAliasRole.Url)]
    [InlineData("MEILISEARCH_URL", DetectedServiceKind.Meilisearch, EnvAliasRole.Url)]
    [InlineData("MEILISEARCH_HOST", DetectedServiceKind.Meilisearch, EnvAliasRole.Url)]
    [InlineData("MEILI_HTTP_ADDR", DetectedServiceKind.Meilisearch, EnvAliasRole.Url)]
    public void A_specific_spelling_is_an_alias(string name, DetectedServiceKind kind, EnvAliasRole role)
    {
        var variable = Var(InspectExample($"{name}=changeme", LibraryFor(kind)), name);

        variable.Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
        variable.Service.Should().Be(kind);
        variable.Role.Should().Be(role);
    }

    [Theory]
    [InlineData("DB_URL", EnvAliasRole.Url)]
    [InlineData("DB_URI", EnvAliasRole.Url)]
    [InlineData("DB_HOST", EnvAliasRole.Host)]
    [InlineData("DATABASE_HOST", EnvAliasRole.Host)]
    [InlineData("DB_PORT", EnvAliasRole.Port)]
    [InlineData("DATABASE_PORT", EnvAliasRole.Port)]
    [InlineData("DB_USER", EnvAliasRole.User)]
    [InlineData("DB_USERNAME", EnvAliasRole.User)]
    [InlineData("DATABASE_USER", EnvAliasRole.User)]
    [InlineData("DB_PASSWORD", EnvAliasRole.Password)]
    [InlineData("DB_PASS", EnvAliasRole.Password)]
    [InlineData("DATABASE_PASSWORD", EnvAliasRole.Password)]
    [InlineData("DB_NAME", EnvAliasRole.Database)]
    [InlineData("DB_DATABASE", EnvAliasRole.Database)]
    [InlineData("DATABASE_NAME", EnvAliasRole.Database)]
    public void A_generic_name_is_an_alias_of_the_one_database(string name, EnvAliasRole role)
    {
        foreach (var (library, kind) in new[] { ("pg", DetectedServiceKind.Postgres), ("mongoose", DetectedServiceKind.MongoDb) })
        {
            var result = InspectExample($"{name}=x", library);
            var variable = Var(result, name);

            variable.Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
            variable.Service.Should().Be(kind);
            variable.Role.Should().Be(role);
            Codes(result).Should().NotContain("database-unclear");
        }
    }

    [Theory]
    [InlineData("DB_HOST")]
    [InlineData("DB_PASSWORD")]
    [InlineData("DATABASE_NAME")]
    public void A_generic_name_with_no_database_needs_an_answer_and_says_why(string name)
    {
        var result = InspectExample($"{name}=somewhere");
        var variable = Var(result, name);

        variable.Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        variable.Service.Should().BeNull();
        variable.ExampleValue.Should().Be("somewhere");
        Codes(result).Should().Contain("database-unclear");
    }

    [Fact]
    public void A_generic_name_with_two_databases_needs_an_answer_and_says_why()
    {
        var result = InspectExample("DB_HOST=x", "pg", "mongoose");

        Var(result, "DB_HOST").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        Codes(result).Should().Contain("database-unclear");
    }

    [Fact]
    public void Redis_is_not_the_database_a_generic_name_means()
    {
        var result = InspectExample("DB_HOST=x", "ioredis");

        Var(result, "DB_HOST").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        Codes(result).Should().Contain("database-unclear");
    }

    [Fact]
    public void A_guess_is_not_a_second_engine_beside_a_certain_one()
    {
        var variable = Var(InspectExample("DB_HOST=x\nMYSQL_HOST=\n", "pg"), "DB_HOST");

        variable.Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
        variable.Service.Should().Be(DetectedServiceKind.Postgres);
    }

    [Fact]
    public void A_generic_name_MariaDB_really_sets_stays_provided_and_the_ones_it_does_not_are_aliases()
    {
        var result = InspectExample("DB_HOST=x\nDB_USER=x\nDB_NAME=x\nDB_USERNAME=x\n", "mariadb");

        Var(result, "DB_HOST").Disposition.Should().Be(EnvVarDisposition.ProvidedByAttach);
        Var(result, "DB_HOST").Service.Should().Be(DetectedServiceKind.MariaDb);
        Var(result, "DB_USER").Disposition.Should().Be(EnvVarDisposition.ProvidedByAttach);

        Var(result, "DB_NAME").Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
        Var(result, "DB_NAME").Service.Should().Be(DetectedServiceKind.MariaDb);
        Var(result, "DB_NAME").Role.Should().Be(EnvAliasRole.Database);
        Var(result, "DB_USERNAME").Role.Should().Be(EnvAliasRole.User);
    }

    [Fact]
    public void A_whole_laravel_example_file_asks_nothing_about_its_database()
    {
        Write("composer.json", "{}");
        Write(".env.example", """
            APP_KEY=
            DB_CONNECTION=pgsql
            DB_HOST=127.0.0.1
            DB_PORT=5432
            DB_DATABASE=laravel
            DB_USERNAME=root
            DB_PASSWORD=
            """);

        var result = Inspect();

        Service(result, DetectedServiceKind.Postgres).Certain.Should().BeTrue();
        foreach (var name in new[] { "DB_HOST", "DB_PORT", "DB_DATABASE", "DB_USERNAME", "DB_PASSWORD" })
            Var(result, name).Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
        Var(result, "DB_CONNECTION").Disposition.Should().Be(EnvVarDisposition.KeepExample);
        Var(result, "APP_KEY").Disposition.Should().Be(EnvVarDisposition.GeneratedSecret);
        Codes(result).Should().NotContain("database-unclear");
    }

    [Fact]
    public void A_database_url_with_nothing_to_say_which_engine_is_the_same_question()
    {
        var result = InspectExample("DATABASE_URL=");

        Var(result, "DATABASE_URL").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        Codes(result).Should().Contain("database-unclear");
    }

    [Fact]
    public void A_database_url_naming_a_file_is_not_a_question_at_all()
    {
        var result = InspectExample("DATABASE_URL=sqlite:///app.db");

        Var(result, "DATABASE_URL").Disposition.Should().Be(EnvVarDisposition.KeepExample);
        Codes(result).Should().NotContain("database-unclear").And.Contain("sqlite-needs-volume");
    }

    // --- Rule 4: secrets Harbora generates ---------------------------------------------------

    [Theory]
    [InlineData("SECRET")]
    [InlineData("SECRET_KEY")]
    [InlineData("APP_SECRET")]
    [InlineData("APP_KEY")]
    [InlineData("JWT_SECRET")]
    [InlineData("JWT_KEY")]
    [InlineData("JWT_SECRET_KEY")]
    [InlineData("SESSION_SECRET")]
    [InlineData("SESSION_KEY")]
    [InlineData("COOKIE_SECRET")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("AUTH_SECRET")]
    [InlineData("NEXTAUTH_SECRET")]
    [InlineData("DJANGO_SECRET_KEY")]
    [InlineData("FLASK_SECRET_KEY")]
    [InlineData("TOKEN_SECRET")]
    [InlineData("ACCESS_TOKEN_SECRET")]
    [InlineData("REFRESH_TOKEN_SECRET")]
    [InlineData("SALT")]
    [InlineData("HASH_SALT")]
    public void An_apps_own_secret_is_generated_and_the_examples_value_is_thrown_away(string name)
    {
        var variable = Var(InspectExample($"{name}=the-value-in-the-example"), name);

        variable.Disposition.Should().Be(EnvVarDisposition.GeneratedSecret);
        variable.ExampleValue.Should().BeNull("an example file's value is never a secret worth keeping");
        variable.Service.Should().BeNull();
    }

    // --- Rule 5: what only a person can answer -----------------------------------------------

    [Theory]
    [InlineData("STRIPE_SECRET_KEY")]
    [InlineData("TELEGRAM_BOT_TOKEN")]
    [InlineData("SMTP_PASSWORD")]
    [InlineData("SMTP_PASS")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("API_BASE_URL")]
    [InlineData("GOOGLE_CREDENTIALS")]
    [InlineData("github_token")]
    public void A_third_partys_credential_needs_an_answer_even_with_a_tidy_value(string name)
    {
        var variable = Var(InspectExample($"{name}=abc123"), name);

        variable.Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        variable.ExampleValue.Should().Be("abc123");
    }

    [Theory]
    [InlineData("")]
    [InlineData("changeme")]
    [InlineData("CHANGEME")]
    [InlineData("change_me")]
    [InlineData("change-me-please")]
    [InlineData("your_bucket")]
    [InlineData("your-bucket")]
    [InlineData("xxx")]
    [InlineData("sk_xxxxxxxx")]
    [InlineData("<bucket>")]
    [InlineData("...")]
    [InlineData("…")]
    [InlineData("TODO")]
    [InlineData("example")]
    [InlineData("https://example.com")]
    [InlineData("hello@example.com")]
    [InlineData("${OTHER_THING}")]
    public void A_placeholder_needs_an_answer(string value)
    {
        Var(InspectExample($"BUCKET={value}"), "BUCKET").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
    }

    [Theory]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("http://[::1]:80")]
    public void A_development_address_needs_an_answer_and_keeps_the_value_to_show(string value)
    {
        var variable = Var(InspectExample($"WEBHOOK={value}"), "WEBHOOK");

        variable.Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        variable.ExampleValue.Should().Be(value);
    }

    // --- Rule 6: everything else -------------------------------------------------------------

    [Theory]
    [InlineData("LOG_LEVEL", "info")]
    [InlineData("WORKERS", "4")]
    [InlineData("FEATURE_X", "true")]
    [InlineData("HOST", "0.0.0.0")]
    [InlineData("SITE_URL", "https://shop.acme.test")]
    public void Everything_else_keeps_the_examples_value(string name, string value)
    {
        var variable = Var(InspectExample($"{name}={value}"), name);

        variable.Disposition.Should().Be(EnvVarDisposition.KeepExample);
        variable.ExampleValue.Should().Be(value);
        variable.Service.Should().BeNull();
        variable.Role.Should().BeNull();
    }

    [Fact]
    public void The_rules_apply_in_order()
    {
        // MEILI_MASTER_KEY has KEY in it, and it is rule 2 that decides. JWT_SECRET has SECRET in it,
        // and it is rule 4. STRIPE_SECRET_KEY has both words and is neither.
        var result = InspectExample("MEILI_MASTER_KEY=x\nJWT_SECRET=x\nSTRIPE_SECRET_KEY=x\nPORT=1\n", "meilisearch");

        Var(result, "MEILI_MASTER_KEY").Disposition.Should().Be(EnvVarDisposition.ProvidedByAttach);
        Var(result, "JWT_SECRET").Disposition.Should().Be(EnvVarDisposition.GeneratedSecret);
        Var(result, "STRIPE_SECRET_KEY").Disposition.Should().Be(EnvVarDisposition.NeedsAnswer);
        result.EnvVars.Should().HaveCount(3);
    }
}
