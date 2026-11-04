using System.IO.Compression;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Launch;

/// <summary>
/// The service and the inspector were written apart, each with a fake of the other. These run the real
/// inspector over a real archive through the real service, because what a folder's path looks like, what
/// a candidate is called and what a role resolves to are all things the two could disagree about while
/// both sets of tests stayed green.
/// </summary>
public sealed class LaunchWithRealInspectorTests
{
    private static async Task<Guid> UploadAsync(LaunchHarness h, string fileName, Dictionary<string, string> files)
    {
        h.Reader.UseRealInspector = true;

        var uploads = Path.Combine(h.WorkDir, "_uploads");
        Directory.CreateDirectory(uploads);
        var path = Path.Combine(uploads, fileName);
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        var started = await h.Request().StartAsync(new LaunchStartRequest
        {
            WorkspaceId = h.WorkspaceId, UserId = h.UserId, Source = LaunchSourceKind.Upload, ArchivePath = path
        }, default);
        started.Ok.Should().BeTrue();
        await h.Worker().InspectAsync(started.Value, default);
        return started.Value;
    }

    private const string NodeWithPostgres =
        """{"name":"shop","scripts":{"start":"node server.js"},"dependencies":{"express":"^4.0.0","pg":"^8.0.0"}}""";

    [Fact]
    public async Task A_node_app_with_postgres_comes_out_as_one_screen_with_one_question()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            ["server.js"] = "require('express')().listen(process.env.PORT)",
            [".env.example"] = "DATABASE_URL=\nDB_HOST=\nJWT_SECRET=\nTELEGRAM_BOT_TOKEN=\nLOG_LEVEL=info\nPORT=4000\n",
            [".env"] = "SOMETHING_ONLY_ON_MY_LAPTOP=hunter2\n"
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal.Should().NotBeNull();
        proposal!.AppName.Should().Be("my-shop");
        proposal.Inspection.Stack.Should().Be(DetectedStack.Node);
        proposal.Defaults.Kind.Should().Be(ServiceKind.Web);
        proposal.Defaults.ContainerPort.Should().Be(4000);

        var service = proposal.Defaults.Services.Should().ContainSingle().Subject;
        service.Type.Should().Be(ManagedServiceType.PostgreSql);
        service.Aliases.Should().Contain(new AttachEnvAlias("DB_HOST", "PGHOST"));

        proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.NeedsAnswer).Select(f => f.Key)
            .Should().Equal("TELEGRAM_BOT_TOKEN");
        proposal.EnvFields.Single(f => f.Key == "JWT_SECRET").Disposition.Should().Be(EnvVarDisposition.GeneratedSecret);
        proposal.EnvFields.Single(f => f.Key == "JWT_SECRET").SuggestedValue.Should().BeNull();
        proposal.EnvFields.Single(f => f.Key == "LOG_LEVEL").SuggestedValue.Should().Be("info");
        proposal.EnvFields.Should().NotContain(f => f.Key == "SOMETHING_ONLY_ON_MY_LAPTOP", ".env never leaves the archive");
    }

    [Fact]
    public async Task A_repository_with_two_apps_asks_which_and_then_proposes_only_for_that_one()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "monorepo-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["api/package.json"] = NodeWithPostgres,
            ["api/server.js"] = "",
            ["api/.env.example"] = "DB_HOST=\nPORT=8080\n",
            ["web/package.json"] = """{"name":"web","scripts":{"start":"node index.js"},"dependencies":{"express":"^4.0.0"}}""",
            ["web/index.js"] = ""
        });

        var asked = await h.Request().ProposeAsync(id, default);
        asked!.Inspection.RootDirectory.Should().BeNull();
        asked.Inspection.RootCandidates.Should().BeEquivalentTo(["api", "web"]);

        var api = await h.Request().ChooseRootAsync(id, "api", default);
        api.Ok.Should().BeTrue();
        api.Value!.Defaults.RootDirectory.Should().Be("api");
        api.Value.Defaults.ContainerPort.Should().Be(8080);
        api.Value.Defaults.Services.Should().ContainSingle().Which.Aliases
            .Should().Contain(new AttachEnvAlias("DB_HOST", "PGHOST"));

        // Changing one's mind reads what was stored for the other folder, not what the first one said.
        var web = await h.Request().ChooseRootAsync(id, "web", default);
        web.Ok.Should().BeTrue();
        web.Value!.Defaults.RootDirectory.Should().Be("web");
        web.Value.Defaults.Services.Should().BeEmpty();
    }

    [Fact]
    public async Task A_zip_that_wraps_everything_in_one_folder_builds_from_that_folder()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "shop-main-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["shop-main/package.json"] = NodeWithPostgres,
            ["shop-main/server.js"] = ""
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal!.Inspection.RootDirectory.Should().Be("shop-main");
        proposal.Defaults.RootDirectory.Should().Be("shop-main");
        proposal.Inspection.Stack.Should().Be(DetectedStack.Node);
        proposal.Defaults.Services.Should().ContainSingle().Which.Type.Should().Be(ManagedServiceType.PostgreSql);
    }

    /// <summary>What Docker itself will take as a container name and a tag's repository, which a test here can only state.</summary>
    private const string WhatDockerAccepts = "^[a-z0-9][a-z0-9_.-]*$";

    [Fact]
    public async Task A_zip_with_a_persian_name_gets_an_address_docker_can_build_and_keeps_its_name()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "فروشگاه-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            ["server.js"] = ""
        });

        var proposal = await h.Request().ProposeAsync(id, default);
        proposal!.AppName.Should().Be("فروشگاه");
        proposal.SuggestedSlug.Should().Be("app");

        (await h.Request().ConfirmAsync(id, proposal.Defaults, null, default)).Ok.Should().BeTrue();

        var app = h.Store().Apps.IgnoreQueryFilters().Single();
        app.Name.Should().Be("فروشگاه", "it is still what the person called it");
        app.Slug.Should().Be("app");
        var database = h.Store().ManagedServices.IgnoreQueryFilters().Single();
        database.Name.Should().StartWith("فروشگاه");
        database.ContainerName.Should().MatchRegex(WhatDockerAccepts);
        database.VolumeName.Should().MatchRegex(WhatDockerAccepts);
        database.DatabaseName.Should().MatchRegex("^[a-z0-9_]+$");
    }

    [Fact]
    public async Task Launching_the_same_repository_twice_previews_the_address_it_would_really_get()
    {
        using var h = new LaunchHarness();
        var first = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new() { ["package.json"] = NodeWithPostgres });
        (await h.Request().ProposeAsync(first, default))!.SuggestedSlug.Should().Be("my-shop");
        await h.ConfirmAsync(first);
        h.Store().Apps.IgnoreQueryFilters().Single().Slug.Should().Be("my-shop");

        var second = await UploadAsync(h, "my-shop-fedcba9876543210fedcba9876543210.zip", new() { ["package.json"] = NodeWithPostgres });
        var proposal = await h.Request().ProposeAsync(second, default);

        proposal!.SuggestedSlug.Should().Be("my-shop-2");

        await h.ConfirmAsync(second);
        h.Store().Apps.IgnoreQueryFilters().Select(a => a.Slug).Should().Contain(proposal.SuggestedSlug!,
            "a preview is only worth showing if it is what happens");
    }

    [Fact]
    public async Task Looking_at_the_screen_reserves_nothing()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new() { ["package.json"] = NodeWithPostgres });

        (await h.Request().ProposeAsync(id, default))!.SuggestedSlug.Should().Be("my-shop");
        (await h.Request().ProposeAsync(id, default))!.SuggestedSlug.Should().Be("my-shop");
    }

    [Fact]
    public async Task A_python_bot_is_a_worker_and_asks_only_for_its_token()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-bot-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["requirements.txt"] = "python-telegram-bot==21.0\n",
            ["bot.py"] = "print('hello')\n",
            ["Procfile"] = "worker: python bot.py\n",
            [".env.example"] = "TELEGRAM_BOT_TOKEN=\n"
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal!.Inspection.Stack.Should().Be(DetectedStack.Python);
        proposal.Defaults.Kind.Should().Be(ServiceKind.Worker);
        proposal.Defaults.Services.Should().BeEmpty();
        proposal.EnvFields.Should().ContainSingle(f => f.Disposition == EnvVarDisposition.NeedsAnswer)
            .Which.Key.Should().Be("TELEGRAM_BOT_TOKEN");
    }

    /// <summary>
    /// A role the engine has no variable for is dropped by <c>AliasesFor</c> without a word and the app
    /// starts without it, so every spelling the inspector can put a role on has to resolve to something.
    /// </summary>
    [Theory]
    [InlineData("pg", ManagedServiceType.PostgreSql)]
    [InlineData("mysql2", ManagedServiceType.MySql)]
    [InlineData("mongoose", ManagedServiceType.MongoDb)]
    public async Task Every_generic_database_spelling_resolves_for_every_database(string dependency, ManagedServiceType type)
    {
        string[] names =
        [
            "DB_URL", "DB_URI", "DB_HOST", "DATABASE_HOST", "DB_PORT", "DATABASE_PORT", "DB_USER", "DB_USERNAME",
            "DATABASE_USER", "DB_PASSWORD", "DB_PASS", "DATABASE_PASSWORD", "DB_NAME", "DB_DATABASE", "DATABASE_NAME"
        ];

        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "app-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = $$$"""{"name":"a","scripts":{"start":"node s.js"},"dependencies":{"{{{dependency}}}":"1.0.0"}}""",
            ["s.js"] = "",
            [".env.example"] = string.Join('\n', names.Select(n => n + "=")) + "\n"
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        var service = proposal!.Defaults.Services.Should().ContainSingle().Subject;
        service.Type.Should().Be(type);
        service.Aliases!.Select(a => a.Name).Should().BeEquivalentTo(names);
        proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.NeedsAnswer).Should().BeEmpty();
    }

    // ---- a name that could be any database, and somebody with their own -----------------------------

    /// <summary>What a person really has: a PDO call with nothing in any manifest to say which engine.</summary>
    private static Dictionary<string, string> PlainPdoApp() => new()
    {
        ["index.php"] = "<?php new PDO('mysql:host=' . getenv('DB_HOST'));",
        [".env.example"] = "DB_HOST=\nDB_PORT=\nDB_NAME=\nDB_USER=\nDB_PASSWORD=\n"
    };

    private static IReadOnlyList<AttachEnvAlias> AliasesOn(LaunchHarness h, ManagedServiceType type)
    {
        var store = h.Store();
        var service = store.ManagedServices.IgnoreQueryFilters().Single(s => s.Type == type);
        return ManagedServiceAttachEnv.ReadAliases(
            store.AppManagedServices.IgnoreQueryFilters().Single(a => a.ManagedServiceId == service.Id).EnvAliasesJson);
    }

    /// <summary>What a deployment of this app would really hand its container, through the one function that decides.</summary>
    private static Dictionary<string, string> WhatTheAppGets(LaunchHarness h)
    {
        var app = h.Store().Apps.IgnoreQueryFilters()
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.ManagedServices).ThenInclude(m => m.ManagedService!).ThenInclude(s => s.Replicas)
            .Include(a => a.ManagedServices).ThenInclude(m => m.Database)
            .Single();

        var protector = new PassthroughProtector();
        return EffectiveEnvironmentBuilder.Compute(app, protector, "")
            .ToDictionary(e => e.Key, e => e.IsSecret ? protector.Unprotect(e.Value) : e.Value, StringComparer.Ordinal);
    }

    private static async Task<string?> NotSetLineAsync(LaunchHarness h, Guid id) =>
        (await h.Request().GetAsync(id, default))!.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail;

    [Fact]
    public async Task A_plain_pdo_app_is_asked_its_names_until_an_engine_is_ticked_and_then_they_are_that_engines()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "pdo-0123456789abcdef0123456789abcdef.zip", PlainPdoApp());

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal!.Defaults.Services.Should().BeEmpty("nothing says which engine, and a guess is not consent");
        proposal.Inspection.Notes.Should().Contain(n => n.Code == "database-unclear");
        proposal.EnvFields.Should().OnlyContain(f => f.Disposition == EnvVarDisposition.NeedsAnswer && f.Service == null);
        proposal.Defaults.EnvVars.Select(v => v.Key).Should().Contain("DB_HOST");

        // What the note on that screen tells the person to do, the way the form posts it.
        var ticked = proposal.Defaults with { Services = [new LaunchServiceChoice(ManagedServiceType.MySql)] };
        (await h.Request().ConfirmAsync(id, ticked, null, default)).Ok.Should().BeTrue();

        AliasesOn(h, ManagedServiceType.MySql).Should().BeEquivalentTo(
        [
            new AttachEnvAlias("DB_HOST", "MYSQL_HOST"),
            new AttachEnvAlias("DB_PORT", "MYSQL_PORT"),
            new AttachEnvAlias("DB_NAME", "MYSQL_DATABASE"),
            new AttachEnvAlias("DB_USER", "MYSQL_USER"),
            new AttachEnvAlias("DB_PASSWORD", "MYSQL_PASSWORD")
        ]);

        WhatTheAppGets(h)["DB_HOST"].Should().Be(WhatTheAppGets(h)["MYSQL_HOST"], "an empty DB_HOST is the failure this is for");
        (await NotSetLineAsync(h, id)).Should().BeNull("every one of them is answered");
    }

    [Fact]
    public async Task What_the_person_typed_for_such_a_name_still_wins()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "pdo-0123456789abcdef0123456789abcdef.zip", PlainPdoApp());
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            Services = [new LaunchServiceChoice(ManagedServiceType.PostgreSql)],
            EnvVars = [new LaunchEnvChoice("DB_HOST", "db.example.com")]
        }, null, default)).Ok.Should().BeTrue();

        var gets = WhatTheAppGets(h);
        gets["DB_HOST"].Should().Be("db.example.com");
        gets["DB_PORT"].Should().Be(gets["PGPORT"], "only the one that was typed");
    }

    [Fact]
    public async Task With_two_engines_ticked_nothing_says_which_so_the_names_stay_questions()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "pdo-0123456789abcdef0123456789abcdef.zip", PlainPdoApp());
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            Services = [new LaunchServiceChoice(ManagedServiceType.MySql), new LaunchServiceChoice(ManagedServiceType.PostgreSql)]
        }, null, default)).Ok.Should().BeTrue();

        AliasesOn(h, ManagedServiceType.MySql).Should().BeEmpty();
        AliasesOn(h, ManagedServiceType.PostgreSql).Should().BeEmpty();
        (await NotSetLineAsync(h, id)).Should().Contain("DB_HOST");
    }

    [Fact]
    public async Task A_cache_ticked_beside_one_database_does_not_make_it_unclear()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "pdo-0123456789abcdef0123456789abcdef.zip", PlainPdoApp());
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            Services = [new LaunchServiceChoice(ManagedServiceType.Redis), new LaunchServiceChoice(ManagedServiceType.MariaDb)]
        }, null, default)).Ok.Should().BeTrue();

        AliasesOn(h, ManagedServiceType.MariaDb).Should().Contain(new AttachEnvAlias("DB_NAME", "DB_DATABASE"));
        AliasesOn(h, ManagedServiceType.Redis).Should().BeEmpty();
    }

    [Fact]
    public async Task Unticking_one_of_two_certain_databases_leaves_the_names_to_the_other_as_the_form_posts_it()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "both-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = """{"scripts":{"start":"node s.js"},"dependencies":{"pg":"8","mysql2":"3"}}""",
            ["s.js"] = "",
            [".env.example"] = "DB_HOST=\n"
        });
        var defaults = await h.DefaultsAsync(id);
        defaults.Services.Should().HaveCount(2);
        defaults.Services.Should().OnlyContain(s => s.Aliases!.Count == 0, "with both ticked nothing says which");

        // The form sends the default's own choice for whatever stayed ticked, aliases and all.
        var kept = defaults.Services.Single(s => s.Type == ManagedServiceType.PostgreSql);
        (await h.Request().ConfirmAsync(id, defaults with { Services = [kept] }, null, default)).Ok.Should().BeTrue();

        AliasesOn(h, ManagedServiceType.PostgreSql).Should().ContainSingle().Which.Should().Be(new AttachEnvAlias("DB_HOST", "PGHOST"));
    }

    [Fact]
    public async Task A_guess_unticked_for_another_engine_hands_its_generic_names_over_and_keeps_its_own()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "app-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = """{"scripts":{"start":"node s.js"}}""",
            ["s.js"] = "",
            // A name that is Postgres's alone makes a weak guess, and DB_HOST then follows that guess.
            [".env.example"] = "POSTGRES_HOST=\nDB_HOST=\n"
        });
        var proposal = await h.Request().ProposeAsync(id, default);
        proposal!.Defaults.Services.Should().BeEmpty();
        proposal.EnvFields.Single(f => f.Key == "DB_HOST").Service.Should().Be(ManagedServiceType.PostgreSql);

        (await h.Request().ConfirmAsync(id, proposal.Defaults with
        {
            Services = [new LaunchServiceChoice(ManagedServiceType.MySql)]
        }, null, default)).Ok.Should().BeTrue();

        var aliases = AliasesOn(h, ManagedServiceType.MySql);
        aliases.Should().ContainSingle().Which.Should().Be(new AttachEnvAlias("DB_HOST", "MYSQL_HOST"));
    }

    [Fact]
    public async Task Every_name_an_attachment_would_set_is_on_the_screen_with_its_engine_but_is_not_a_question()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            [".env.example"] = "DATABASE_URL=\nPGHOST=\nDB_HOST=\n"
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        var address = proposal!.EnvFields.Single(f => f.Key == "DATABASE_URL");
        address.Disposition.Should().Be(EnvVarDisposition.ProvidedByAttach);
        address.Service.Should().Be(ManagedServiceType.PostgreSql);
        address.IsSecret.Should().BeTrue("the address of somebody's own database has its password in it");
        address.SuggestedValue.Should().BeNull();

        var host = proposal.EnvFields.Single(f => f.Key == "PGHOST");
        host.Service.Should().Be(ManagedServiceType.PostgreSql);
        host.IsSecret.Should().BeFalse();

        var alias = proposal.EnvFields.Single(f => f.Key == "DB_HOST");
        alias.Disposition.Should().Be(EnvVarDisposition.AliasOfAttach);
        alias.Service.Should().Be(ManagedServiceType.PostgreSql);

        proposal.Defaults.EnvVars.Select(v => v.Key).Should().NotContain(["DATABASE_URL", "PGHOST", "DB_HOST"],
            "a null value for one of these would say it is not set, for an app that has it attached");

        await h.ConfirmAsync(id);
        (await NotSetLineAsync(h, id)).Should().BeNull();
        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task Somebody_with_their_own_database_unticks_it_and_gives_its_address()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            [".env.example"] = "DATABASE_URL=\n"
        });
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            Services = [],
            EnvVars = [new LaunchEnvChoice("DATABASE_URL", "postgres://me:hunter2@ep-1.neon.tech/shop", IsSecret: true)]
        }, null, default)).Ok.Should().BeTrue();

        h.Store().ManagedServices.IgnoreQueryFilters().Should().BeEmpty();
        WhatTheAppGets(h)["DATABASE_URL"].Should().Be("postgres://me:hunter2@ep-1.neon.tech/shop");
        h.Store().EnvironmentVariables.IgnoreQueryFilters().Single().IsSecret.Should().BeTrue();
        (await NotSetLineAsync(h, id)).Should().BeNull();
    }

    [Fact]
    public async Task A_value_given_for_such_a_name_outranks_the_attachment_when_the_engine_is_ticked_as_well()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            [".env.example"] = "DATABASE_URL=\n"
        });
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            EnvVars = [new LaunchEnvChoice("DATABASE_URL", "postgres://mine/shop", IsSecret: true)]
        }, null, default)).Ok.Should().BeTrue();

        h.Store().ManagedServices.IgnoreQueryFilters().Should().ContainSingle();
        var gets = WhatTheAppGets(h);
        gets["DATABASE_URL"].Should().Be("postgres://mine/shop");
        gets["PGHOST"].Should().NotBeNullOrEmpty("only that one name is the person's");
    }

    [Fact]
    public async Task A_blank_for_an_attach_name_of_a_ticked_engine_sets_nothing_and_is_not_called_unset()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            [".env.example"] = "DATABASE_URL=\n"
        });
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            EnvVars = [new LaunchEnvChoice("DATABASE_URL", null, IsSecret: true)]
        }, null, default)).Ok.Should().BeTrue();

        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().BeEmpty();
        (await NotSetLineAsync(h, id)).Should().BeNull("its attachment sets it");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_the_engine_unticked_and_no_value_given_the_name_is_answered_by_nothing_and_is_said(bool postedBlank)
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-shop-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = NodeWithPostgres,
            [".env.example"] = "DATABASE_URL=\n"
        });
        var defaults = await h.DefaultsAsync(id);

        (await h.Request().ConfirmAsync(id, defaults with
        {
            Services = [],
            EnvVars = postedBlank ? [new LaunchEnvChoice("DATABASE_URL", null)] : []
        }, null, default)).Ok.Should().BeTrue();

        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().BeEmpty();
        (await NotSetLineAsync(h, id)).Should().Contain("DATABASE_URL");
    }

    private const string ViteShaped =
        """{"name":"site","scripts":{"dev":"vite","build":"vite build"},"devDependencies":{"vite":"5"}}""";

    [Fact]
    public async Task A_vite_app_is_proposed_as_a_frontend_on_the_web_with_no_start_command()
    {
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-site-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = ViteShaped,
            ["index.html"] = "<html></html>",
            [".env.example"] = "VITE_API_URL=\nVITE_TITLE=Shop\n"
        });

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal!.Inspection.Stack.Should().Be(DetectedStack.Frontend);
        proposal.Defaults.Kind.Should().Be(ServiceKind.Web);
        proposal.Defaults.ContainerPort.Should().Be(80);
        proposal.Defaults.Command.Should().BeNull();
        proposal.Defaults.ReleaseCommand.Should().BeNull();
        proposal.Defaults.Services.Should().BeEmpty();
        proposal.Inspection.Notes.Should().ContainSingle(n => n.Code == "public-variables-read-at-build");
        proposal.EnvFields.Single(f => f.Key == "VITE_TITLE").SuggestedValue.Should().Be("Shop");
    }

    [Theory]
    [InlineData("web: node dist/index.js\n", ServiceKind.Web, "node dist/index.js")]
    [InlineData("worker: node dist/jobs.js\n", ServiceKind.Worker, "node dist/jobs.js")]
    public async Task A_procfiles_start_command_reaches_the_app_and_keeps_the_build_node(
        string procfile, ServiceKind kind, string command)
    {
        const string packageJson = """{"name":"api","scripts":{"build":"tsc"}}""";
        using var h = new LaunchHarness();
        var id = await UploadAsync(h, "my-api-0123456789abcdef0123456789abcdef.zip", new()
        {
            ["package.json"] = packageJson,
            ["Procfile"] = procfile
        });

        var proposal = await h.Request().ProposeAsync(id, default);
        proposal!.Inspection.Stack.Should().Be(DetectedStack.Node);
        proposal.Defaults.Command.Should().Be(command);

        var confirmed = await h.Request().ConfirmAsync(id, proposal.Defaults, null, default);
        confirmed.Ok.Should().BeTrue(string.Join("; ", confirmed.Refusals.Select(r => r.Message)));

        var app = h.Store().Apps.IgnoreQueryFilters().Single();
        app.StartCommand.Should().Be(command);
        app.Kind.Should().Be(kind);

        // And what the pipeline does with exactly those two fields, over the same files.
        var tree = Path.Combine(h.WorkDir, "_tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "package.json"), packageJson);

        Harbora.Infrastructure.Deployments.Buildpacks.Detect(tree, app.ContainerPort, app.Kind, app.StartCommand)!.Value
            .Stack.Should().Be("Node.js");
    }
}
