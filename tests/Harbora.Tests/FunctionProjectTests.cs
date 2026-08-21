using FluentAssertions;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Functions;
using Xunit;

namespace Harbora.Tests;
/// <summary>
/// What "code typed in the panel" turns into.
///
/// <para>
/// The generator is the whole feature: everything after it is the deployment pipeline that already
/// existed. It is also the only part testable on a machine with no Docker, which is the machine this
/// was written on — so these assertions are the evidence for the generated hosts, and a container
/// actually answering is a server step, claimed nowhere else.
/// </para>
/// </summary>
public class FunctionProjectTests
{
    private static FunctionDefinition Fn(
        string slug, FunctionTrigger trigger = FunctionTrigger.Http,
        string? route = null, string code = "// code", bool enabled = true, bool isPublic = false) =>
        new()
        {
            Name = slug,
            Slug = slug,
            Trigger = trigger,
            Route = route,
            Code = code,
            IsEnabled = enabled,
            IsPublic = isPublic,
            CronExpression = trigger == FunctionTrigger.Cron ? "0 3 * * *" : null,
            EventKey = trigger == FunctionTrigger.Event ? FunctionEvents.DeploymentSucceeded : null
        };

    private static string File(IReadOnlyList<FunctionProject.GeneratedFile> files, string path) =>
        files.Should().ContainSingle(f => f.Path == path).Subject.Content;

    [Theory]
    [InlineData(FunctionRuntime.CSharp)]
    [InlineData(FunctionRuntime.JavaScript)]
    [InlineData(FunctionRuntime.Python)]
    public void Every_runtime_produces_a_dockerfile_the_pipeline_will_find(FunctionRuntime runtime)
    {
        var files = FunctionProject.Generate(runtime, [Fn("hello")]);

        // The app's DockerfilePath is set to exactly this name, so stack detection is never
        // consulted for a function app — and the build log never claims to have detected a stack.
        var dockerfile = File(files, "Dockerfile.harbora");
        dockerfile.Should().Contain("FROM");
        dockerfile.Should().Contain(FunctionProject.DefaultPort.ToString());
    }

    [Theory]
    [InlineData(FunctionRuntime.CSharp)]
    [InlineData(FunctionRuntime.JavaScript)]
    [InlineData(FunctionRuntime.Python)]
    public void Every_runtime_serves_the_health_path_and_the_invoke_door(FunctionRuntime runtime)
    {
        var files = FunctionProject.Generate(runtime, [Fn("hello")]);
        var host = string.Concat(files.Select(f => f.Content));

        // The health path is what the deployment's own probe hits, so a host that did not answer it
        // would fail every publish; the invoke prefix is where cron and events knock.
        host.Should().Contain(FunctionProject.HealthPath);
        host.Should().Contain(FunctionProject.InvokePathPrefix);
        host.Should().Contain(FunctionProject.SecretEnvVar);
        host.Should().Contain(FunctionProject.SecretHeader);
    }

    [Fact]
    public void The_csharp_host_dispatches_by_name_rather_than_by_reflection()
    {
        var files = FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("send-report")]);

        // Naming the type explicitly is what turns a misspelled handler into a compile error in the
        // build log — at publish time — instead of a 404 from a function that was never registered.
        File(files, "Program.cs").Should().Contain("Harbora.Fn.SendReport.Function.Run");
        File(files, "functions/send-report.cs").Should().Contain("namespace Harbora.Fn.SendReport;");
    }

    [Fact]
    public void The_csharp_wrapper_keeps_the_users_code_intact()
    {
        var code = "public static class Function { public static Task<FnResponse> Run(FnRequest r, FnContext c) => null!; }";

        var files = FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("a", code: code)]);

        File(files, "functions/a.cs").Should().Contain(code);
    }

    [Fact]
    public void The_javascript_host_imports_every_function()
    {
        var files = FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("a"), Fn("b-c")]);
        var server = File(files, "server.mjs");

        server.Should().Contain("import fn_a from './functions/a.mjs';");
        server.Should().Contain("import fn_b_c from './functions/b-c.mjs';");
        server.Should().Contain("slug: 'b-c'");
    }

    [Fact]
    public void The_python_host_imports_modules_whose_names_python_accepts()
    {
        var files = FunctionProject.Generate(FunctionRuntime.Python, [Fn("send-report")]);

        // A hyphen is legal in a slug and illegal in a module name; the file and the import have to
        // agree on the substitution or the container dies on its first line.
        File(files, "server.py").Should().Contain("from functions import send_report as fn_send_report");
        files.Should().ContainSingle(f => f.Path == "functions/send_report.py");
    }

    [Fact]
    public void A_route_defaults_to_the_slug_and_an_explicit_one_wins()
    {
        FunctionProject.RouteFor(Fn("hello")).Should().Be("hello");
        FunctionProject.RouteFor(Fn("hello", route: "/webhooks/stripe/")).Should().Be("webhooks/stripe");
    }

    [Fact]
    public void A_disabled_function_is_published_but_marked_off()
    {
        // Kept in the image on purpose: switching it back on is then a database write rather than a
        // rebuild, which is what the 3am switch has to be.
        var files = FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("a", enabled: false)]);

        File(files, "server.mjs").Should().Contain("enabled: false");
        files.Should().ContainSingle(f => f.Path == "functions/a.mjs");
    }

    // ---------------------------------------------------- F1: the public door

    [Fact]
    public void A_new_functions_registry_row_says_protected_in_every_runtime()
    {
        // Protected is the default: a function that never touched the new toggle must be exactly as
        // closed as it was before this flag existed.
        File(FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("hello")]), "Program.cs")
            .Should().Contain("\"hello\", \"hello\", \"http\", true, false,");

        File(FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("hello")]), "server.mjs")
            .Should().Contain("enabled: true, public: false,");

        File(FunctionProject.Generate(FunctionRuntime.Python, [Fn("hello")]), "server.py")
            .Should().Contain("'enabled': True, 'public': False,");
    }

    [Fact]
    public void A_public_functions_registry_row_says_so_in_every_runtime()
    {
        File(FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("hello", isPublic: true)]), "Program.cs")
            .Should().Contain("\"hello\", \"hello\", \"http\", true, true,");

        File(FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("hello", isPublic: true)]), "server.mjs")
            .Should().Contain("enabled: true, public: true,");

        File(FunctionProject.Generate(FunctionRuntime.Python, [Fn("hello", isPublic: true)]), "server.py")
            .Should().Contain("'enabled': True, 'public': True,");
    }

    [Fact]
    public void The_visitor_route_refuses_a_protected_function_with_401_in_every_runtime()
    {
        // This is the acceptance test for "a protected one still 401s": the visitor route (not the
        // panel's own invoke door, which already 401s an unsigned caller) now carries its own refusal
        // for exactly the function rows that never opted into being public.
        File(FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("hello")]), "Program.cs")
            .Should().Contain("if (!fn.Public) return Results.StatusCode(401);");

        var js = File(FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("hello")]), "server.mjs");
        js.Should().Contain("if (!fn.public)");
        js.Should().Contain("status: 401");

        var python = File(FunctionProject.Generate(FunctionRuntime.Python, [Fn("hello")]), "server.py");
        python.Should().Contain("if not fn['public']:");
        python.Should().Contain("self._send(401, {}, '')");
    }

    // ------------------------------------------------------- F1 reversal: the host reports back

    [Theory]
    [InlineData(FunctionRuntime.CSharp)]
    [InlineData(FunctionRuntime.JavaScript)]
    [InlineData(FunctionRuntime.Python)]
    public void Every_runtime_reads_the_report_url_and_carries_the_secret_header_on_it(FunctionRuntime runtime)
    {
        var host = string.Concat(FunctionProject.Generate(runtime, [Fn("hello", isPublic: true)]).Select(f => f.Content));

        host.Should().Contain(FunctionProject.ReportUrlEnvVar);
        // The report is authenticated with the same header/secret the panel's own invoke door checks —
        // never a second credential.
        host.Should().Contain(FunctionProject.SecretHeader);
    }

    [Fact]
    public void The_csharp_visitor_route_reports_but_the_panels_own_invoke_door_does_not()
    {
        var program = File(FunctionProject.Generate(FunctionRuntime.CSharp, [Fn("hello", isPublic: true)]), "Program.cs");

        // Exactly one call site passes report: true (the visitor route); the invoke door passes false —
        // a call the panel already made and will complete from its own response must never also be
        // reported by the host, which would be a second, uncorrelated row for the same call.
        program.Should().Contain("report: true");
        program.Should().Contain("report: false");
    }

    [Fact]
    public void The_javascript_visitor_route_reports_but_the_panels_own_invoke_door_does_not()
    {
        var server = File(FunctionProject.Generate(FunctionRuntime.JavaScript, [Fn("hello", isPublic: true)]), "server.mjs");

        server.Should().Contain("res, true);");
        server.Should().Contain("res, false);");
    }

    [Fact]
    public void The_python_visitor_route_reports_but_the_panels_own_invoke_door_does_not()
    {
        var server = File(FunctionProject.Generate(FunctionRuntime.Python, [Fn("hello", isPublic: true)]), "server.py");

        server.Should().Contain("report=True");
        // The invoke door's own call site never passes report at all — it relies on the parameter's
        // own False default, the same "nothing extra to say" the C#/JS call sites make explicit.
        server.Should().Contain("def _run(self, fn, request, ctx, report=False):");
    }

    [Theory]
    [InlineData(FunctionRuntime.CSharp)]
    [InlineData(FunctionRuntime.JavaScript)]
    [InlineData(FunctionRuntime.Python)]
    public void The_secret_door_keeps_demanding_the_secret_even_for_a_public_function(FunctionRuntime runtime)
    {
        // A Public function gets an *additional* route, never a replacement: the panel's own door —
        // where cron, events and "Run now" arrive — must still refuse an unsigned call exactly as it
        // always did, whatever this function's exposure is set to.
        var files = FunctionProject.Generate(runtime, [Fn("hello", isPublic: true)]);
        var host = string.Concat(files.Select(f => f.Content));

        host.Should().Contain("The panel's own door: cron and events arrive here, never through a public route.");
        host.Should().Contain(FunctionProject.SecretHeader);
        host.Should().Contain(FunctionProject.SecretEnvVar);
    }

    [Fact]
    public void Generation_is_deterministic_regardless_of_row_order()
    {
        // Docker layer caching keys off content. An unstable ordering would rebuild every layer on
        // a publish that changed nothing.
        var one = FunctionProject.Generate(FunctionRuntime.Python, [Fn("b"), Fn("a")]);
        var two = FunctionProject.Generate(FunctionRuntime.Python, [Fn("a"), Fn("b")]);

        File(one, "server.py").Should().Be(File(two, "server.py"));
    }

    [Fact]
    public void An_unusable_identifier_is_refused_rather_than_written_into_a_host()
    {
        var bad = new FunctionDefinition { Name = "Bad One", Slug = "Bad One", Code = "x" };

        var act = () => FunctionProject.Generate(FunctionRuntime.CSharp, [bad]);

        // It would otherwise become a type name with a space in it, and the only symptom would be a
        // compiler error in a build log about generated code the person never wrote.
        act.Should().Throw<InvalidOperationException>().WithMessage("*Bad One*");
    }

    [Fact]
    public void A_quote_in_a_route_cannot_break_out_of_the_generated_literal()
    {
        var files = FunctionProject.Generate(
            FunctionRuntime.JavaScript, [Fn("a", route: "we'ird")]);

        // Escaped rather than refused here: the route is validated before it is stored, so this is
        // the second line — but an unescaped quote would not fail to build, it would silently route
        // somewhere nobody asked for.
        File(files, "server.mjs").Should().Contain(@"route: 'we\'ird'");
    }

    [Fact]
    public void The_invoke_envelope_carries_the_trigger_and_the_event()
    {
        var envelope = FunctionProject.InvokeEnvelope("event",
            FunctionEvent.Create(FunctionEvents.BackupFailed, Guid.NewGuid(), "nightly", ("reason", "disk full")));

        envelope.Should().Contain("\"trigger\":\"event\"");
        envelope.Should().Contain(FunctionEvents.BackupFailed);
        envelope.Should().Contain("disk full");
    }

    [Fact]
    public void A_cron_invocation_carries_no_event()
    {
        FunctionProject.InvokeEnvelope("cron", null).Should().Contain("\"event\":null");
    }
}

/// <summary>
/// The identifier a function's name becomes.
///
/// <para>
/// It is a folder, a type name, an import binding and a URL segment at once. Anything legal in three
/// of them and not the fourth fails at image build time, in a log, long after somebody typed it.
/// </para>
/// </summary>
public class FunctionSlugTests
{
    [Theory]
    [InlineData("Send Report", "send-report")]
    [InlineData("  Hello  ", "hello")]
    [InlineData("a//b", "a-b")]
    [InlineData("Émigré", "migr")]
    public void Names_become_narrow_identifiers(string input, string expected) =>
        FunctionSlug.Normalise(input).Should().Be(expected);

    [Fact]
    public void An_identifier_never_starts_with_a_digit()
    {
        // It becomes a C# type name and a JavaScript binding, and neither may begin with one.
        FunctionSlug.Normalise("2fa").Should().StartWith("fn-");
        FunctionSlug.IsValid(FunctionSlug.Normalise("2fa")).Should().BeTrue();
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_produces_nothing()
    {
        // Empty rather than a made-up name: the caller refuses, instead of the platform inventing an
        // address the person never chose.
        FunctionSlug.Normalise("—").Should().BeEmpty();
        FunctionSlug.IsValid("").Should().BeFalse();
    }

    [Fact]
    public void Normalising_is_idempotent()
    {
        var once = FunctionSlug.Normalise("Send Report!");
        FunctionSlug.Normalise(once).Should().Be(once);
    }

    [Theory]
    [InlineData("send-report", "SendReport")]
    [InlineData("a", "A")]
    public void Pascal_case_is_what_the_csharp_host_declares(string slug, string expected) =>
        FunctionSlug.ToPascalCase(slug).Should().Be(expected);
}
