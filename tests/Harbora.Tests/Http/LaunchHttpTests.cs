using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Navigation;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The launch pages as requests: what each state of a launch draws, what the form turns into, and
/// what is refused. The service behind them is a script, so what these prove is the page's half of the
/// bargain. The real one is <c>tests/Harbora.Tests/Launch/</c>'s.
///
/// <para>
/// Persian is what this harness renders by default, and Razor writes it out as numeric character
/// references, so a page is decoded before a sentence is looked for in it.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LaunchHttpTests : IClassFixture<LaunchPanelFixture>
{
    private readonly LaunchPanelFixture _fixture;
    private static int _nextUser;

    public LaunchHttpTests(LaunchPanelFixture fixture)
    {
        _fixture = fixture;
        fixture.Launches.Reset();
    }

    private FakeLaunchService Launches => _fixture.Launches;

    private Task<HttpClient> Owner() =>
        _fixture.SignedInAs(_fixture.WorkspaceId, $"launch-owner-{Interlocked.Increment(ref _nextUser)}@example.com");

    private Task<HttpClient> Viewer() =>
        _fixture.SignedInAs(_fixture.WorkspaceId, $"launch-viewer-{Interlocked.Increment(ref _nextUser)}@example.com", SystemRole.Viewer);

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} should render");
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static async Task SpeakEnglish(HttpClient client)
    {
        var token = await client.AntiforgeryTokenFrom("/launch");
        (await client.PostFormAsync("/account/language", token, ("culture", "en"), ("returnUrl", "/")))
            .StatusCode.Should().Be(HttpStatusCode.Found);
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    private static LaunchStepView Step(string key, LaunchStepState state, string? detailFa = null, string? link = null) =>
        new(key, key + " step", "مرحله‌ی " + key, state, detailFa is null ? null : "detail of " + key, detailFa, link);

    private static IReadOnlyList<LaunchStepView> Steps(
        LaunchStepState source = LaunchStepState.Done, LaunchStepState services = LaunchStepState.Waiting,
        LaunchStepState app = LaunchStepState.Waiting, LaunchStepState deploy = LaunchStepState.Waiting,
        LaunchStepState verify = LaunchStepState.Waiting) =>
    [
        Step(LaunchStepKeys.Source, source), Step(LaunchStepKeys.Services, services), Step(LaunchStepKeys.App, app),
        Step(LaunchStepKeys.Deploy, deploy), Step(LaunchStepKeys.Verify, verify)
    ];

    private static LaunchView View(
        LaunchStatus status,
        Guid? id = null,
        string source = "https://github.com/you/shop",
        Guid? appId = null,
        string? appName = null,
        string? publicUrl = null,
        Guid? deploymentId = null,
        string? failure = null,
        string? failureFa = null,
        IReadOnlyList<LaunchStepView>? steps = null,
        IReadOnlyList<AppCheck>? checks = null,
        bool canRetry = false,
        bool? terminal = null,
        LaunchSourceKind kind = LaunchSourceKind.Git) =>
        new(id ?? Guid.CreateVersion7(), status, kind, source, appId, appName, publicUrl, deploymentId, failure, failureFa,
            steps ?? Steps(), checks ?? [], canRetry,
            terminal ?? status is LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems or LaunchStatus.Failed
                or LaunchStatus.InspectFailed or LaunchStatus.Abandoned);

    private Guid Given(LaunchView view, LaunchProposal? proposal = null, LaunchQuote? quote = null, Guid? workspace = null)
    {
        Launches.Add(workspace ?? _fixture.WorkspaceId, view, proposal, quote);
        return view.Id;
    }

    private static DetectedService Detected(DetectedServiceKind kind, bool certain, string file = "package.json", string reason = "\"pg\" in dependencies") =>
        new(kind, [new ServiceEvidence(file, reason)], certain);

    private static SourceInspection Inspection(
        DetectedStack stack = DetectedStack.Node,
        DetectedAppKind kind = DetectedAppKind.Web,
        IReadOnlyList<string>? roots = null,
        IReadOnlyList<DetectedEnvVar>? env = null,
        IReadOnlyList<InspectionNote>? notes = null,
        string? root = null) =>
        new(stack, kind, root, roots ?? [], 3000, null, null, [], env ?? [], notes ?? []);

    /// <summary>A launch at the confirm screen that found a certain Postgres, a maybe-Redis and four kinds of variable.</summary>
    private static (LaunchView View, LaunchProposal Proposal) Confirmable(
        SourceInspection? inspection = null,
        IReadOnlyList<LaunchEnvField>? fields = null,
        ServiceKind appKind = ServiceKind.Web)
    {
        var view = View(LaunchStatus.AwaitingConfirmation);
        IReadOnlyList<AttachEnvAlias> aliases = [new AttachEnvAlias("DB_HOST", "PGHOST")];
        var proposal = new LaunchProposal(
            view.Id,
            inspection ?? Inspection(env:
            [
                new DetectedEnvVar("STRIPE_SECRET_KEY", EnvVarDisposition.NeedsAnswer, "sk_test_example", null, null, ".env.example"),
                new DetectedEnvVar("SUPPORT_EMAIL", EnvVarDisposition.NeedsAnswer, "you@example.com", null, null, ".env.example")
            ], notes: [new InspectionNote("env-file-ignored", "A real .env was left out.", "فایل .env واقعی کنار گذاشته شد.")]),
            new LaunchChoices
            {
                Name = "shop",
                Kind = appKind,
                ContainerPort = 3000,
                InstanceSizeKey = "small",
                EnvironmentId = null,
                Services = [new LaunchServiceChoice(ManagedServiceType.PostgreSql, Aliases: aliases)]
            },
            "shop",
            fields ??
            [
                new LaunchEnvField("STRIPE_SECRET_KEY", EnvVarDisposition.NeedsAnswer, null, IsSecret: true, ".env.example"),
                new LaunchEnvField("SUPPORT_EMAIL", EnvVarDisposition.NeedsAnswer, null, IsSecret: false, ".env.example"),
                new LaunchEnvField("JWT_SECRET", EnvVarDisposition.GeneratedSecret, null, IsSecret: true, ".env.example"),
                new LaunchEnvField("SESSION_SECRET", EnvVarDisposition.GeneratedSecret, null, IsSecret: true, ".env.example"),
                new LaunchEnvField("LOG_LEVEL", EnvVarDisposition.KeepExample, "info", IsSecret: false, ".env.example"),
                new LaunchEnvField("DB_HOST", EnvVarDisposition.AliasOfAttach, null, IsSecret: false, ".env.example")
            ],
            [
                new LaunchServiceOption(ManagedServiceType.PostgreSql, "PostgreSQL", "پستگرس", Detected(DetectedServiceKind.Postgres, certain: true)),
                new LaunchServiceOption(ManagedServiceType.Redis, "Redis", "ردیس", Detected(DetectedServiceKind.Redis, certain: false, ".env.example", "REDIS_URL named")),
                new LaunchServiceOption(ManagedServiceType.MongoDb, "MongoDB", "مونگو", null)
            ]);
        return (view, proposal);
    }

    private static readonly LaunchQuote Charged = new(
        [new LaunchQuoteLine("App", "اپ", 1_000_000), new LaunchQuoteLine("Postgres", "پستگرس", 200_000)],
        FirstHourMinor: 1_200_000, BalanceMinor: 30_000_000, "IRT", Exempt: false, Refusal: null);

    private static string CheckRow(string html, string key)
    {
        var at = html.IndexOf($"data-check=\"{key}\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, $"the {key} check should be on the page");
        var start = html.LastIndexOf("<li", at, StringComparison.Ordinal);
        return html[start..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    /// <summary>The first element carrying <paramref name="marker"/>, as a tag.</summary>
    private static string Tag(string html, string marker)
    {
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, $"{marker} should be on the page");
        return html[html.LastIndexOf('<', at)..(html.IndexOf('>', at) + 1)];
    }

    // ---- the question ------------------------------------------------------------------------------

    [Fact]
    public async Task The_question_asks_one_thing_and_offers_the_two_other_ways_quietly()
    {
        var html = await PageAsync(await Owner(), "/launch");

        html.Should().Contain("کدت کجاست؟");
        html.Should().Contain("از گیت").And.Contain("از کامپیوترت");
        html.Should().Contain("name=\"GitUrl\"").And.Contain("https://github.com/you/your-app");
        html.Should().Contain("مخزن خصوصی است؟ یا شاخه‌ی دیگری می‌خواهی؟");

        // A real file input in a real multipart form, which is what makes it work with no script.
        html.Should().Contain("type=\"file\"").And.Contain("accept=\".zip,.tar.gz,.tgz\"");
        html.Should().Contain("enctype=\"multipart/form-data\"");

        html.Should().Contain("href=\"/templates\"").And.Contain("href=\"/apps/create\"");
    }

    [Fact]
    public async Task The_question_reads_in_english_too()
    {
        var client = await Owner();
        await SpeakEnglish(client);

        var html = await PageAsync(client, "/launch");

        html.Should().Contain("Where is your code?").And.Contain("From your computer");
        html.Should().NotContain("کدت کجاست؟");
    }

    [Fact]
    public async Task Somebody_who_cannot_create_apps_is_told_so_instead_of_being_offered_forms_that_refuse()
    {
        var html = await PageAsync(await Viewer(), "/launch");

        html.Should().Contain("data-launch-no-permission");
        html.Should().NotContain("data-launch-git").And.NotContain("data-launch-upload");
    }

    // ---- starting ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_repository_url_starts_a_launch_and_lands_on_its_page()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync("/launch", token,
            ("GitUrl", "  https://github.com/you/shop  "), ("GitRef", ""), ("GitToken", "ghp_secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/launch/{Launches.NextStartId}");

        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Git);
        started.CloneUrl.Should().Be("https://github.com/you/shop");
        started.GitRef.Should().BeNull("a blank branch means the repository's default");
        started.GitToken.Should().Be("ghp_secret");
        started.ArchivePath.Should().BeNull();
        started.WorkspaceId.Should().Be(_fixture.WorkspaceId);
        started.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_refused_start_comes_back_to_the_form_with_what_was_typed_but_never_the_token()
    {
        Launches.StartRefusal = new ServiceRefusal("Only http and https.", "فقط http و https.");
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync("/launch", token,
            ("GitUrl", "git@github.com:you/shop.git"), ("GitRef", "develop"), ("GitToken", "ghp_never_again"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("فقط http و https.");
        html.Should().Contain("value=\"git@github.com:you/shop.git\"").And.Contain("value=\"develop\"");
        html.Should().NotContain("ghp_never_again");
    }

    [Fact]
    public async Task Nothing_at_all_is_refused_on_the_form_before_it_reaches_the_service()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync("/launch", token, ("GitUrl", "   "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).Should().Contain("آدرس مخزن را بنویس");
        Launches.Started.Should().BeEmpty();
    }

    private static MultipartFormDataContent Upload(string token, byte[] bytes, string fileName, string field = "File") =>
        new()
        {
            { new StringContent(token), HttpConversation.AntiforgeryField },
            { new ByteArrayContent(bytes), field, fileName }
        };

    private static byte[] Zip(int size = 64)
    {
        var bytes = new byte[size];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        return bytes;
    }

    private IEnumerable<string> UploadedFiles()
    {
        var folder = Path.Combine(_fixture.WorkDir, "_uploads");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories) : [];
    }

    [Fact]
    public async Task An_upload_is_written_under_the_upload_directory_and_handed_to_the_launch()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");
        var bytes = Zip();

        var response = await client.PostAsync("/launch", Upload(token, bytes, "my-shop.zip"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/launch/{Launches.NextStartId}");

        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Upload);
        started.CloneUrl.Should().BeNull();
        started.ArchivePath.Should().NotBeNull();
        started.ArchivePath!.Should().StartWith(Path.Combine(_fixture.WorkDir, "_uploads"));
        (await File.ReadAllBytesAsync(started.ArchivePath!)).Should().Equal(bytes);

        // The path is all the launch has to name the app from, so the person's name has to survive in it.
        LaunchSource.LabelForArchive(started.ArchivePath!).Should().Be("my-shop.zip");
    }

    [Theory]
    [InlineData("..\\..\\evil.zip", "evil.zip")]
    [InlineData("../../evil.tar.gz", "evil.tar.gz")]
    [InlineData("what is this?.tgz", "what-is-this.tgz")]
    [InlineData(".zip", "source.zip")]
    [InlineData("no-extension", "no-extension.zip")]
    public async Task What_a_browser_says_a_file_is_called_cannot_write_outside_the_upload_directory(string claimed, string stored)
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        await client.PostAsync("/launch", Upload(token, Zip(), claimed));

        var started = Launches.Started.Should().ContainSingle().Subject;
        LaunchSource.LabelForArchive(started.ArchivePath!).Should().Be(stored);
        Path.GetFullPath(started.ArchivePath!).Should().StartWith(Path.GetFullPath(Path.Combine(_fixture.WorkDir, "_uploads")));
    }

    [Fact]
    public async Task A_refused_start_deletes_the_upload_and_says_why_on_the_form()
    {
        Launches.StartRefusal = new ServiceRefusal("Suspended.", "این فضای کاری معلق است.");
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostAsync("/launch", Upload(token, Zip(), "refused-one.zip"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).Should().Contain("این فضای کاری معلق است.");

        var path = Launches.Started.Should().ContainSingle().Subject.ArchivePath!;
        File.Exists(path).Should().BeFalse("a launch that never started owns nothing");
    }

    [Fact]
    public async Task A_start_that_blows_up_still_deletes_the_upload()
    {
        Launches.StartThrows = true;
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostAsync("/launch", Upload(token, Zip(), "blown-up.zip"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        File.Exists(Launches.Started.Should().ContainSingle().Subject.ArchivePath!).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_that_is_not_an_archive_is_refused_on_the_form_and_never_written()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");
        var before = UploadedFiles().Count();

        var response = await client.PostAsync("/launch", Upload(token, "%PDF-1.7 not a zip"u8.ToArray(), "report.zip"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).Should().Contain("این فایل zip نیست");
        Launches.Started.Should().BeEmpty();
        UploadedFiles().Count().Should().Be(before);
    }

    [Fact]
    public async Task A_tar_gz_is_as_good_as_a_zip()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");
        var bytes = Zip();
        bytes[0] = 0x1F;
        bytes[1] = 0x8B;

        var response = await client.PostAsync("/launch", Upload(token, bytes, "shop.tar.gz"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    [Fact]
    public async Task The_form_limit_really_is_raised_past_its_128_megabyte_default()
    {
        // [RequestSizeLimit] alone is not enough, and nothing about it says so: the form reader has a
        // limit of its own, and past it the request ends on an error page that says nothing about size.
        // Sending more than the default is the only way to see it, so that is what this does.
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostAsync("/launch", Upload(token, Zip(129 * 1024 * 1024), "big.zip"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var path = Launches.Started.Should().ContainSingle().Subject.ArchivePath!;
        new FileInfo(path).Length.Should().Be(129 * 1024 * 1024);
        File.Delete(path);
    }

    // ---- who may, and whose ------------------------------------------------------------------------

    private static string[] PostRoutes(Guid id) =>
    [
        "/launch",
        $"/launch/{id}/root",
        $"/launch/{id}/quote",
        $"/launch/{id}/confirm",
        $"/launch/{id}/retry",
        $"/launch/{id}/abandon"
    ];

    [Fact]
    public async Task Every_post_is_refused_without_an_antiforgery_token()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);
        var client = await Owner();

        foreach (var route in PostRoutes(id))
        {
            var response = await client.PostFormWithoutTokenAsync(route, ("Name", "shop"), ("GitUrl", "https://x.example/y"));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{route} must ask for the token");
        }

        Launches.Started.Should().BeEmpty();
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Quoted.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/root")]
    [InlineData("/confirm")]
    [InlineData("/retry")]
    [InlineData("/abandon")]
    public async Task Somebody_who_cannot_create_apps_is_refused_every_act(string suffix)
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);
        var client = await Viewer();
        var token = await client.AntiforgeryTokenFrom("/apps");

        var path = suffix.Length == 0 ? "/launch" : $"/launch/{id}{suffix}";
        var response = await client.PostFormAsync(path, token, ("GitUrl", "https://x.example/y"), ("Name", "shop"), ("folder", "services/api"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Launches.Started.Should().BeEmpty();
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Retried.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_workspaces_launch_is_404_on_every_route_and_nothing_reaches_the_service()
    {
        var (view, proposal) = Confirmable();
        var theirs = Given(view, proposal, workspace: _fixture.OtherWorkspaceId);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        (await client.GetAsync($"/launch/{theirs}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/launch/{theirs}/progress")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var route in PostRoutes(theirs).Skip(1))
            (await client.PostFormAsync(route, token, ("Name", "shop"), ("folder", "services/api"))).StatusCode.Should().Be(HttpStatusCode.NotFound, route);

        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Quoted.Should().BeEmpty();
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Retried.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_launch_nobody_has_heard_of_is_404()
    {
        (await (await Owner()).GetAsync($"/launch/{Guid.CreateVersion7()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- reading -----------------------------------------------------------------------------------

    [Fact]
    public async Task While_reading_it_says_so_calmly_and_polls()
    {
        var id = Given(View(LaunchStatus.Inspecting, source: "https://github.com/you/big"));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("دارم کدت را می‌خوانم").And.Contain("https://github.com/you/big");
        html.Should().Contain($"data-launch-poll=\"/launch/{id}/progress\"");
        html.Should().Contain("data-launch-slow", "the sentence the script reveals after a minute");
        html.Should().Contain("data-launch-refresh", "with no script there is still a way to look again");
        Tag(html, "data-launch-slow").Should().Contain("hidden");
    }

    [Fact]
    public async Task A_failed_read_is_the_form_again_with_what_was_typed_and_says_nothing_was_created()
    {
        var id = Given(View(LaunchStatus.InspectFailed, source: "https://github.com/you/typo",
            failure: "Could not clone it.", failureFa: "نشد کلونش کنم؛ شاید آدرس اشتباه است یا مخزن خصوصی است."));

        var html = await PageAsync(await Owner(), $"/launch/{id}?branch=develop");

        html.Should().Contain("نشد کلونش کنم");
        html.Should().Contain("چیزی ساخته نشد");
        html.Should().Contain("value=\"https://github.com/you/typo\"").And.Contain("value=\"develop\"");
        html.Should().Contain("data-launch-git").And.Contain("data-launch-upload");

        // Nothing alarming: this is a typo or a private repository, not an outage.
        html.Should().NotContain("alert-danger");
        html.Should().NotContain("اپت بالاست");
    }

    [Fact]
    public async Task A_failed_read_of_an_upload_does_not_put_a_file_name_in_the_url_field()
    {
        var id = Given(View(LaunchStatus.InspectFailed, source: "shop.zip", kind: LaunchSourceKind.Upload,
            failure: "Not an archive.", failureFa: "این فایل خوانده نشد."));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("این فایل خوانده نشد.");
        html.Should().NotContain("value=\"shop.zip\"");
    }

    [Fact]
    public async Task A_branch_typed_with_a_url_travels_to_the_page_that_may_have_to_draw_it_again()
    {
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync("/launch", token, ("GitUrl", "https://x.example/y"), ("GitRef", "feature/one"));

        response.Headers.Location!.OriginalString.Should().Be($"/launch/{Launches.NextStartId}?branch=feature%2Fone");
    }

    // ---- the confirm screen ------------------------------------------------------------------------

    [Fact]
    public async Task The_confirm_screen_says_what_it_found_what_it_will_make_and_what_it_needs()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, Charged);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("این را پیدا کردم");
        html.Should().Contain("یک اپ Node.js که روی وب جواب می‌دهد");
        html.Should().Contain("data-launch-note=\"env-file-ignored\"").And.Contain("فایل .env واقعی کنار گذاشته شد.");

        html.Should().Contain("این‌ها ساخته می‌شوند");
        html.Should().Contain("shop.apps.example.com", "the address as it will be");

        // Certain is ticked and says why; a guess is not ticked and says that it is one.
        Tag(html, "name=\"Services\" value=\"PostgreSql\"").Should().Contain("checked");
        Tag(html, "name=\"Services\" value=\"Redis\"").Should().NotContain("checked");
        html.Should().Contain("شاید لازم باشد");
        Regex.Replace(html, "<[^>]+>", "").Should().Contain("چون pg در package.json هست");

        // And what the inspector missed is one fold away, unticked.
        html.Should().Contain("یک دیتابیس دیگر اضافه کن");
        Tag(html, "name=\"Services\" value=\"MongoDb\"").Should().NotContain("checked");

        html.Should().Contain("data-launch-generated").And.Contain("JWT_SECRET").And.Contain("SESSION_SECRET");

        html.Should().Contain("فقط این‌ها را از تو لازم دارم");
        html.Should().Contain("data-launch-field=\"STRIPE_SECRET_KEY\"").And.Contain("data-launch-field=\"SUPPORT_EMAIL\"");

        html.Should().Contain("راه بینداز");
        Tag(html, "data-launch-submit").Should().NotContain(" disabled=");
    }

    [Fact]
    public async Task Details_are_closed_and_hold_everything_a_person_does_not_have_to_think_about()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        Tag(html, "data-launch-details").Should().NotContain(" open");

        var details = html[html.IndexOf("data-launch-details", StringComparison.Ordinal)..];
        foreach (var field in new[] { "name=\"Slug\"", "name=\"Kind\"", "name=\"ContainerPort\"", "name=\"Command\"", "name=\"ReleaseCommand\"", "name=\"RootDirectory\"" })
            details.Should().Contain(field);

        // A kept example and an alias live here and not in the block that asks.
        details.Should().Contain("data-launch-field=\"LOG_LEVEL\"").And.Contain("value=\"info\"");
        details.Should().Contain("data-launch-aliases").And.Contain("DB_HOST");

        var asked = html[html.IndexOf("data-launch-asked", StringComparison.Ordinal)..html.IndexOf("data-launch-details", StringComparison.Ordinal)];
        asked.Should().NotContain("LOG_LEVEL").And.NotContain("DB_HOST").And.NotContain("JWT_SECRET");
    }

    [Fact]
    public async Task With_nothing_to_ask_that_block_is_simply_not_there()
    {
        var (view, proposal) = Confirmable(fields:
        [
            new LaunchEnvField("JWT_SECRET", EnvVarDisposition.GeneratedSecret, null, IsSecret: true, ".env.example")
        ]);
        var id = Given(view, proposal);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().NotContain("data-launch-asked");
        html.Should().NotContain("فقط این‌ها را از تو لازم دارم");
    }

    [Fact]
    public async Task A_question_is_labelled_with_its_variable_shows_the_example_and_masks_a_secret()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        var secret = Tag(html, "name=\"Values[STRIPE_SECRET_KEY]\"");
        secret.Should().Contain("type=\"password\"").And.Contain("placeholder=\"sk_test_example\"");

        var plain = Tag(html, "name=\"Values[SUPPORT_EMAIL]\"");
        plain.Should().Contain("type=\"text\"").And.Contain("placeholder=\"you@example.com\"");

        html.Should().Contain("شاید اپ بدون این بالا نیاید");
    }

    [Fact]
    public async Task A_bot_reads_as_a_bot_and_gets_no_address()
    {
        var (view, proposal) = Confirmable(Inspection(DetectedStack.Python, DetectedAppKind.Worker), appKind: ServiceKind.Worker);
        var id = Given(view, proposal);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("یک ربات Python که پس‌زمینه اجرا می‌شود (آدرس وب لازم ندارد)");
        Tag(html, "data-launch-web-only").Should().Contain("hidden", "it needs no address, and the line is only there for if somebody changes the kind");
    }

    [Fact]
    public async Task A_frontend_reads_as_something_that_will_be_built_and_served_in_both_languages()
    {
        var (view, proposal) = Confirmable(Inspection(DetectedStack.Frontend, DetectedAppKind.Web));
        var id = Given(view, proposal);
        var client = await Owner();

        (await PageAsync(client, $"/launch/{id}")).Should().Contain("یک فرانت‌اند که اول ساخته می‌شود و بعد خروجی‌اش روی وب نشان داده می‌شود");

        await SpeakEnglish(client);
        (await PageAsync(client, $"/launch/{id}")).Should().Contain("A frontend that will be built, with what it produces served on the web");
    }

    [Fact]
    public async Task A_name_the_platform_keeps_for_itself_says_that_and_does_not_blame_the_root_domain()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ValidateRefusals = [new ServiceRefusal("No.", "نه.")];

        var html = await BodyAsync(await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "status-shop")));

        html.Should().Contain("این اسم برای خود پنل نگه داشته شده");
        html.Should().NotContain("دامنه‌ی اصلی ندارد");
    }

    [Fact]
    public async Task An_unknown_stack_says_it_does_not_know_rather_than_guessing()
    {
        var (view, proposal) = Confirmable(Inspection(DetectedStack.Unknown));
        var id = Given(view, proposal);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("نفهمیدم این کد با چه چیزی نوشته شده");
        html.Should().NotContain("یک اپ  که");
    }

    // ---- several apps in one repository ------------------------------------------------------------

    private static readonly string[] Folders = ["services/api", "services/web", "tools/cron"];

    /// <summary>A launch of a several-app repository, with no folder chosen yet.</summary>
    private (Guid Id, LaunchProposal Proposal) GivenSeveralApps(string? chosen = null)
    {
        var (view, proposal) = Confirmable(Inspection(roots: Folders, root: chosen));
        return (Given(view, proposal, Charged), proposal);
    }

    [Fact]
    public async Task A_repository_with_several_apps_asks_only_which_one()
    {
        var (id, _) = GivenSeveralApps();

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("این مخزن چند اپ دارد. کدام را راه بیندازم؟");
        foreach (var folder in Folders)
            Tag(html, $"name=\"folder\" value=\"{folder}\"").Should().StartWith("<button").And.Contain("type=\"submit\"");
        html.Should().Contain($"action=\"/launch/{id}/root\"");

        // Nothing it could propose is about the app they mean, so none of it is on the page.
        foreach (var absent in new[]
                 {
                     "data-launch-confirm", "data-launch-found", "data-launch-created", "data-launch-asked",
                     "data-launch-cost", "data-launch-submit", "data-launch-details", "name=\"Services\""
                 })
            html.Should().NotContain(absent);

        Launches.Quoted.Should().BeEmpty("there is no figure to show until there is an app to price");
    }

    [Fact]
    public async Task The_folder_question_reads_in_english_too()
    {
        var (id, _) = GivenSeveralApps();
        var client = await Owner();
        await SpeakEnglish(client);

        var html = await PageAsync(client, $"/launch/{id}");

        html.Should().Contain("This repository holds several apps. Which one?");
        html.Should().NotContain("این مخزن چند اپ دارد");
    }

    [Fact]
    public async Task Choosing_a_folder_goes_through_the_service_and_lands_on_the_normal_screen()
    {
        var (id, _) = GivenSeveralApps();
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/root", token, ("folder", "services/web"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/launch/{id}");
        Launches.ChosenRoots.Should().Equal((id, "services/web"));

        var html = await PageAsync(client, $"/launch/{id}");
        html.Should().Contain("data-launch-confirm").And.Contain("data-launch-submit").And.Contain("data-launch-cost");
        html.Should().NotContain("این مخزن چند اپ دارد");
    }

    [Fact]
    public async Task An_unknown_folder_is_refused_on_the_same_question()
    {
        var (id, _) = GivenSeveralApps();
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/root", token, ("folder", "../../etc"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("این پوشه در این مخزن نیست.");
        html.Should().Contain("این مخزن چند اپ دارد. کدام را راه بیندازم؟");
        Tag(html, "name=\"folder\" value=\"services/api\"").Should().StartWith("<button");
        html.Should().NotContain("data-launch-submit").And.NotContain("data-launch-cost");
    }

    [Fact]
    public async Task No_folder_at_all_is_refused_on_the_same_question()
    {
        var (id, _) = GivenSeveralApps();
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/root", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).Should().Contain("این پوشه در این مخزن نیست.");
    }

    [Fact]
    public async Task Somebody_who_cannot_create_apps_is_told_so_and_offered_no_folders()
    {
        var (id, _) = GivenSeveralApps();

        var html = await PageAsync(await Viewer(), $"/launch/{id}");

        html.Should().Contain("این مخزن چند اپ دارد").And.Contain("data-launch-no-permission");
        html.Should().NotContain("name=\"folder\"");
    }

    [Fact]
    public async Task Confirming_or_pricing_before_a_folder_is_chosen_does_nothing_and_returns_to_the_question()
    {
        var (id, _) = GivenSeveralApps();
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var confirm = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("RootDirectory", "services/web"));
        var quote = await client.PostFormAsync($"/launch/{id}/quote", token, ("Name", "shop"));

        confirm.StatusCode.Should().Be(HttpStatusCode.Found);
        confirm.RedirectPath().Should().Be($"/launch/{id}");
        quote.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Quoted.Should().BeEmpty();
    }

    [Fact]
    public async Task Once_a_folder_is_chosen_the_screen_says_which_and_offers_the_others()
    {
        var (id, _) = GivenSeveralApps(chosen: "services/web");

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        Regex.Replace(html[html.IndexOf("data-launch-folder", StringComparison.Ordinal)..], "<[^>]+>", "")
            .Should().Contain("این را از پوشه‌ی services/web خواندم.");
        html.Should().Contain("پوشه‌ی دیگری را انتخاب کن");
        Tag(html, "data-launch-other-folder").Should().NotContain(" open");

        Tag(html, "name=\"folder\" value=\"services/api\"").Should().Contain("form=\"launch-other-folder\"");
        Tag(html, "name=\"folder\" value=\"tools/cron\"").Should().Contain("form=\"launch-other-folder\"");
        html.Should().NotContain("name=\"folder\" value=\"services/web\"", "the one already chosen is not another folder");

        // The buttons sit inside the confirm form but belong to this one, which carries its own token.
        var at = html.IndexOf("id=\"launch-other-folder\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(html.IndexOf("</form>", StringComparison.Ordinal), "a form cannot sit inside a form");
        var small = html[at..html.IndexOf("</form>", at, StringComparison.Ordinal)];
        small.Should().Contain($"action=\"/launch/{id}/root\"").And.Contain(HttpConversation.AntiforgeryField);
    }

    [Fact]
    public async Task Changing_the_folder_from_the_screen_goes_through_the_service()
    {
        var (id, _) = GivenSeveralApps(chosen: "services/web");
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/root", token, ("folder", "tools/cron"));

        response.RedirectPath().Should().Be($"/launch/{id}");
        Launches.ChosenRoots.Should().Equal((id, "tools/cron"));
        Regex.Replace(await PageAsync(client, $"/launch/{id}"), "<[^>]+>", "").Should().Contain("این را از پوشه‌ی tools/cron خواندم.");
    }

    [Fact]
    public async Task The_chosen_folder_is_the_services_fact_and_a_field_cannot_disagree_with_it()
    {
        var (id, _) = GivenSeveralApps(chosen: "services/web");
        var client = await Owner();

        var html = await PageAsync(client, $"/launch/{id}");
        html.Should().NotContain("name=\"RootDirectory\"");

        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");
        await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("RootDirectory", "tools/cron"));

        Launches.Confirmed.Should().ContainSingle().Which.Choices.RootDirectory.Should().Be("services/web");
    }

    [Fact]
    public async Task A_single_app_keeps_the_plain_folder_field_and_no_folder_sentence()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);
        var client = await Owner();

        var html = await PageAsync(client, $"/launch/{id}");
        html.Should().Contain("name=\"RootDirectory\"");
        html.Should().NotContain("data-launch-folder").And.NotContain("launch-other-folder");

        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");
        await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("RootDirectory", "api"));

        Launches.Confirmed.Should().ContainSingle().Which.Choices.RootDirectory.Should().Be("api");
    }

    // ---- the cost ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_cost_is_one_sentence_with_the_figures_the_panel_writes_money_in()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, Charged);

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        var text = Regex.Replace(html, "<[^>]+>", "");
        text.Should().Contain("الان 12,000.00 IRT از موجودی کم می‌شود (یک ساعت اجرا)، بعد ساعتی 12,000.00 IRT. موجودی: 300,000.00 IRT.");
    }

    [Fact]
    public async Task An_exempt_workspace_is_told_nothing_comes_off()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, new LaunchQuote(Charged.Lines, 0, null, "IRT", Exempt: true, Refusal: null));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("هزینه‌ای از این فضای کاری کم نمی‌شود.");
        html.Should().NotContain("از موجودی کم می‌شود");
    }

    [Fact]
    public async Task A_refused_quote_takes_the_sentences_place_links_to_billing_and_disables_the_button()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, new LaunchQuote(Charged.Lines, 1_200_000, 5, "IRT", Exempt: false,
            new ServiceRefusal("Not enough balance.", "موجودی کافی نیست.")));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("موجودی کافی نیست.").And.Contain("href=\"/billing\"");
        html.Should().NotContain("از موجودی کم می‌شود");
        Tag(html, "data-launch-submit").Should().Contain(" disabled=");
    }

    [Fact]
    public async Task The_quote_post_answers_with_the_cost_alone_for_what_the_form_says_now()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, Charged);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");
        Launches.Quoted.Clear();

        var response = await client.PostFormAsync($"/launch/{id}/quote", token,
            ("Name", "shop"), ("Services", "Redis"), ("InstanceSizeKey", "large"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("data-launch-cost-state").And.Contain("data-refused=\"false\"");
        html.Should().NotContain("<html", "this replaces one sentence in a page that is already there");

        var asked = Launches.Quoted.Should().ContainSingle().Subject.Choices;
        asked.InstanceSizeKey.Should().Be("large");
        asked.Services.Select(s => s.Type).Should().Equal(ManagedServiceType.Redis);
    }

    [Fact]
    public async Task A_quote_for_a_launch_already_past_the_screen_is_404_and_the_figure_stays()
    {
        var id = Given(View(LaunchStatus.Deploying));
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        (await client.PostFormAsync($"/launch/{id}/quote", token, ("Name", "shop"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- confirming --------------------------------------------------------------------------------

    private async Task<(Guid Id, HttpClient Client, string Token)> AtTheConfirmScreen(LaunchProposal? custom = null, LaunchView? customView = null)
    {
        var (view, proposal) = Confirmable();
        var id = Given(customView ?? view, custom ?? proposal, Charged);
        var client = await Owner();
        return (id, client, await client.AntiforgeryTokenFrom($"/launch/{id}"));
    }

    [Fact]
    public async Task Confirming_turns_the_form_into_choices_and_lands_on_the_progress_page()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        var body = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Name", "my-shop"), new("Slug", "shop-one"), new("Kind", "Web"), new("ContainerPort", "8080"),
            new("Command", "npm start"), new("ReleaseCommand", "npm run migrate"), new("InstanceSizeKey", "medium"),
            new("Services", "PostgreSql"), new("Services", "MongoDb"),
            // Something no option offers, and a variable nobody asked about: somebody's answer to a question this server did not ask.
            new("Services", "Oracle"), new("Values[EVIL]", "x"),
            new("Values[STRIPE_SECRET_KEY]", "sk_live_real"), new("Values[SUPPORT_EMAIL]", ""), new("Values[LOG_LEVEL]", "debug")
        };
        var response = await client.PostAsync($"/launch/{id}/confirm", new FormUrlEncodedContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/launch/{id}");

        var choices = Launches.Confirmed.Should().ContainSingle().Subject.Choices;
        choices.Name.Should().Be("my-shop");
        choices.Slug.Should().Be("shop-one");
        choices.Kind.Should().Be(ServiceKind.Web);
        choices.ContainerPort.Should().Be(8080);
        choices.Command.Should().Be("npm start");
        choices.ReleaseCommand.Should().Be("npm run migrate");
        choices.InstanceSizeKey.Should().Be("medium");

        choices.Services.Select(s => s.Type).Should().BeEquivalentTo([ManagedServiceType.PostgreSql, ManagedServiceType.MongoDb]);
        choices.Services.Single(s => s.Type == ManagedServiceType.PostgreSql).Aliases.Should().ContainSingle()
            .Which.Name.Should().Be("DB_HOST", "the aliases the proposal worked out go with the service");

        var variables = choices.EnvVars.ToDictionary(v => v.Key);
        variables.Keys.Should().BeEquivalentTo(["STRIPE_SECRET_KEY", "JWT_SECRET", "SESSION_SECRET", "LOG_LEVEL"],
            "a blank answer is not set, an alias is not a variable, and EVIL was never asked");
        variables["STRIPE_SECRET_KEY"].Value.Should().Be("sk_live_real");
        variables["STRIPE_SECRET_KEY"].IsSecret.Should().BeTrue("the proposal says so, not the form");
        variables["JWT_SECRET"].Value.Should().BeNull("null is what asks the service to generate it");
        variables["JWT_SECRET"].IsSecret.Should().BeTrue();
        variables["LOG_LEVEL"].Value.Should().Be("debug");
        variables["LOG_LEVEL"].IsSecret.Should().BeFalse();
    }

    [Fact]
    public async Task Blanking_the_name_goes_back_to_the_suggested_one_rather_than_stopping_anybody()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "   "));

        Launches.Confirmed.Should().ContainSingle().Which.Choices.Name.Should().Be("shop");
    }

    [Fact]
    public async Task A_refusal_comes_back_with_what_was_typed_still_there()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ValidateRefusals = [new ServiceRefusal("That name is taken.", "این اسم گرفته شده.", "Name")];

        var body = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Name", "typed-name"), new("Command", "typed command"),
            new("Services", "MongoDb"), new("Values[SUPPORT_EMAIL]", "typed@example.com"), new("Values[LOG_LEVEL]", "typed-level")
        };
        var response = await client.PostAsync($"/launch/{id}/confirm", new FormUrlEncodedContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("این اسم گرفته شده.");

        Tag(html, "name=\"Name\"").Should().Contain("value=\"typed-name\"");
        Tag(html, "name=\"Command\"").Should().Contain("value=\"typed command\"");
        Tag(html, "name=\"Values[SUPPORT_EMAIL]\"").Should().Contain("value=\"typed@example.com\"");
        Tag(html, "name=\"Values[LOG_LEVEL]\"").Should().Contain("value=\"typed-level\"");

        // What was unticked stays unticked and what was ticked stays ticked, including inside its fold.
        Tag(html, "name=\"Services\" value=\"PostgreSql\"").Should().NotContain("checked");
        Tag(html, "name=\"Services\" value=\"MongoDb\"").Should().Contain("checked");
        Tag(html, "data-launch-more-services").Should().Contain("open");

        html.Should().Contain("typed-name.apps.example.com", "the address follows the name that was typed");
        Launches.Confirmed.Should().BeEmpty("a refused screen changes nothing");
    }

    [Fact]
    public async Task A_secrets_value_is_never_drawn_back_and_the_screen_says_to_type_it_again()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ValidateRefusals = [new ServiceRefusal("No.", "نه.")];

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token,
            ("Name", "shop"), ("Values[STRIPE_SECRET_KEY]", "sk_live_never_echoed"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().NotContain("sk_live_never_echoed");
        Tag(html, "name=\"Values[STRIPE_SECRET_KEY]\"").Should().NotContain("value=");
        html.Should().Contain("data-launch-retype");
    }

    [Fact]
    public async Task Nobody_is_told_to_retype_a_key_they_did_not_type()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ValidateRefusals = [new ServiceRefusal("No.", "نه.")];

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"));

        (await BodyAsync(response)).Should().NotContain("data-launch-retype");
    }

    [Fact]
    public async Task A_refusal_about_something_folded_away_unfolds_it()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ValidateRefusals = [new ServiceRefusal("Slug taken.", "این اسم کوتاه گرفته شده.", "Slug")];

        var html = await BodyAsync(await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("Slug", "taken")));

        Tag(html, "data-launch-details").Should().Contain("open");
    }

    [Fact]
    public async Task A_port_that_is_not_a_number_is_refused_rather_than_quietly_becoming_the_guess()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("ContainerPort", "eighty"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("پورت باید عدد باشد.");
        Tag(html, "name=\"ContainerPort\"").Should().Contain("value=\"eighty\"");
        Tag(html, "data-launch-details").Should().Contain("open");
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_port_typed_on_a_persian_keyboard_is_still_a_number()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"), ("ContainerPort", "۸۰۸۰"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Launches.Confirmed.Should().ContainSingle().Which.Choices.ContainerPort.Should().Be(8080);
    }

    [Fact]
    public async Task A_second_submit_lands_on_the_progress_page_and_creates_nothing_twice()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        var first = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"));
        var second = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"));

        first.RedirectPath().Should().Be($"/launch/{id}");
        second.StatusCode.Should().Be(HttpStatusCode.Found, "not an error: they pressed it twice and want to see it going");
        second.RedirectPath().Should().Be($"/launch/{id}");
        Launches.Confirmed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_confirm_refused_before_anything_moved_stays_on_the_form()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ConfirmRefusals = [new ServiceRefusal("Balance ran out.", "موجودی همین الان تمام شد.")];

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "typed-name"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().Contain("موجودی همین الان تمام شد.");
        Tag(html, "name=\"Name\"").Should().Contain("value=\"typed-name\"");
    }

    [Fact]
    public async Task A_confirm_that_stopped_part_way_is_the_timelines_to_explain()
    {
        var (id, client, token) = await AtTheConfirmScreen();
        Launches.ConfirmRefusals = [new ServiceRefusal("The app was refused.", "اپ قبول نشد.")];
        Launches.StatusAfterRefusedConfirm = LaunchStatus.Failed;

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token, ("Name", "shop"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be($"/launch/{id}");
    }

    [Fact]
    public async Task The_no_script_recheck_redraws_the_screen_and_confirms_nothing()
    {
        var (id, client, token) = await AtTheConfirmScreen();

        var response = await client.PostFormAsync($"/launch/{id}/confirm", token,
            ("Name", "typed-name"), ("Recheck", "1"), ("Values[STRIPE_SECRET_KEY]", "sk_live_never_echoed"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        Tag(html, "name=\"Name\"").Should().Contain("value=\"typed-name\"");
        html.Should().NotContain("sk_live_never_echoed");
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Somebody_who_cannot_create_apps_sees_the_screen_with_no_button_to_be_refused_by()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, Charged);

        var html = await PageAsync(await Viewer(), $"/launch/{id}");

        html.Should().NotContain("data-launch-submit").And.NotContain("data-launch-abandon");
        html.Should().Contain("data-launch-no-permission");
    }

    [Fact]
    public async Task The_confirm_screen_reads_in_english_too()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal, Charged);
        var client = await Owner();
        await SpeakEnglish(client);

        var html = await PageAsync(client, $"/launch/{id}");

        html.Should().Contain("Here's what I found").And.Contain("A Node.js app that answers on the web");
        html.Should().Contain("This is what gets created").And.Contain("I only need this from you");
        Regex.Replace(html, "<[^>]+>", "").Should().Contain("12,000.00 IRT comes off your balance now");
        html.Should().Contain("A real .env was left out.");
    }

    // ---- the timeline ------------------------------------------------------------------------------

    [Theory]
    [InlineData(LaunchStatus.Creating, "دارم می‌سازمش")]
    [InlineData(LaunchStatus.WaitingForServices, "منتظرم دیتابیس آماده شود")]
    [InlineData(LaunchStatus.Deploying, "دارم اجرایش می‌کنم")]
    [InlineData(LaunchStatus.Verifying, "دارم امتحانش می‌کنم")]
    public async Task A_launch_in_flight_says_what_it_is_doing_polls_and_claims_nothing(LaunchStatus status, string line)
    {
        var id = Given(View(status));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain(line);
        html.Should().Contain("data-launch-poll").And.Contain("data-terminal=\"false\"");
        html.Should().Contain("data-launch-refresh");
        html.Should().NotContain("اپت بالاست").And.NotContain("data-launch-outcome=\"succeeded\"");
    }

    [Fact]
    public async Task Each_step_draws_its_own_state_and_only_a_done_one_is_green()
    {
        var steps = Steps(LaunchStepState.Done, LaunchStepState.Skipped, LaunchStepState.Running, LaunchStepState.Waiting, LaunchStepState.Waiting);
        var id = Given(View(LaunchStatus.Creating, steps: steps));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        string Row(string key)
        {
            var at = html.IndexOf($"data-launch-step=\"{key}\"", StringComparison.Ordinal);
            return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
        }

        Row(LaunchStepKeys.Source).Should().Contain("data-lucide=\"circle-check\"").And.Contain("text-ok");
        Row(LaunchStepKeys.Services).Should().Contain("data-lucide=\"circle-minus\"").And.NotContain("text-ok");
        Row(LaunchStepKeys.App).Should().Contain("animate-spin").And.NotContain("text-ok");
        Row(LaunchStepKeys.Deploy).Should().Contain("data-lucide=\"circle\"").And.NotContain("text-ok");
    }

    [Fact]
    public async Task The_deploy_step_links_to_the_page_the_deployment_really_answers_at()
    {
        // Not /deployments/{id}: nothing routes that. The step's own link is not trusted to know.
        var deployment = Guid.CreateVersion7();
        IReadOnlyList<LaunchStepView> steps =
        [
            Step(LaunchStepKeys.Source, LaunchStepState.Done),
            Step(LaunchStepKeys.Deploy, LaunchStepState.Running, link: $"/deployments/{deployment}")
        ];
        var id = Given(View(LaunchStatus.Deploying, deploymentId: deployment, steps: steps));
        var client = await Owner();

        var html = await PageAsync(client, $"/launch/{id}");

        html.Should().Contain($"href=\"/deployments/details/{deployment}\"").And.Contain("لاگ زنده");
        html.Should().NotContain($"href=\"/deployments/{deployment}\"");
    }

    [Fact]
    public async Task The_progress_route_answers_with_the_changing_part_alone()
    {
        var id = Given(View(LaunchStatus.Deploying));

        var response = await (await Owner()).GetAsync($"/launch/{id}/progress");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await BodyAsync(response);
        html.Should().NotContain("<html");
        html.Should().Contain("data-launch-state").And.Contain("data-status=\"Deploying\"").And.Contain("data-terminal=\"false\"");
    }

    [Theory]
    [InlineData(LaunchStatus.Succeeded)]
    [InlineData(LaunchStatus.SucceededWithProblems)]
    [InlineData(LaunchStatus.Failed)]
    public async Task A_finished_launch_tells_the_poll_to_stop(LaunchStatus status)
    {
        var id = Given(View(status));

        var html = await BodyAsync(await (await Owner()).GetAsync($"/launch/{id}/progress"));

        html.Should().Contain("data-terminal=\"true\"").And.Contain("data-reload=\"false\"");
    }

    [Theory]
    [InlineData(LaunchStatus.AwaitingConfirmation)]
    [InlineData(LaunchStatus.InspectFailed)]
    public async Task Arriving_at_a_page_of_its_own_tells_the_poll_to_reload(LaunchStatus status)
    {
        var id = Given(View(status));

        var html = await BodyAsync(await (await Owner()).GetAsync($"/launch/{id}/progress"));

        html.Should().Contain("data-reload=\"true\"");
    }

    private static readonly IReadOnlyList<AppCheck> MixedChecks =
    [
        new("running", AppCheckStatus.Passed, "Running", "در حال اجراست"),
        new("url", AppCheckStatus.Skipped, "Address", "آدرس", "No domain.", "دامنه ندارد."),
        new("logs", AppCheckStatus.Failed, "Logs", "لاگ‌ها", "It complains.", "دارد شکایت می‌کند.", "Read them.", "بخوانش.", "/apps/x/logs")
    ];

    [Fact]
    public async Task Success_is_the_address_and_the_list_of_what_was_really_checked()
    {
        var appId = Guid.CreateVersion7();
        IReadOnlyList<AppCheck> checks =
        [
            new("running", AppCheckStatus.Passed, "Running", "در حال اجراست"),
            new("url", AppCheckStatus.Skipped, "Address", "آدرس", "No domain.", "دامنه ندارد.")
        ];
        var id = Given(View(LaunchStatus.Succeeded, appId: appId, appName: "shop",
            publicUrl: "https://shop.apps.example.com", checks: checks));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("اپت بالاست");
        html.Should().Contain("href=\"https://shop.apps.example.com\"");
        html.Should().Contain("data-check=\"running\"");
        html.Should().Contain($"href=\"/apps/details/{appId}\"").And.Contain("برو به صفحه‌ی اپ");

        // "We could not check" and "it works" are different sentences.
        CheckRow(html, "running").Should().Contain("text-ok");
        CheckRow(html, "url").Should().NotContain("text-ok");
        html.Should().Contain("data-launch-skipped-note");
    }

    [Theory]
    [InlineData("shop.apps.example.com")]
    [InlineData("httpbin.apps.example.com")]
    public async Task A_host_with_no_scheme_still_opens_as_a_link(string host)
    {
        var id = Given(View(LaunchStatus.Succeeded, appId: Guid.CreateVersion7(), publicUrl: host));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain($"href=\"https://{host}\"");
    }

    [Fact]
    public async Task A_bot_that_is_running_reads_as_one_and_links_to_its_logs()
    {
        var appId = Guid.CreateVersion7();
        _fixture.Seed(db => db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = _fixture.WorkspaceId, EnvironmentId = _fixture.EnvironmentId,
            Name = "bot", Slug = "launch-bot-" + appId.ToString("N")[..8], Kind = ServiceKind.Worker, SourceType = AppSourceType.Upload
        }));
        var id = Given(View(LaunchStatus.Succeeded, appId: appId, appName: "bot"));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("ربات اجرا شده");
        html.Should().Contain($"href=\"/apps/{appId}/logs\"");
        html.Should().NotContain("اپت بالاست").And.NotContain("data-launch-address-link");
    }

    [Fact]
    public async Task A_web_app_with_no_address_says_that_rather_than_reading_like_a_bot()
    {
        var appId = Guid.CreateVersion7();
        _fixture.Seed(db => db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = _fixture.WorkspaceId, EnvironmentId = _fixture.EnvironmentId,
            Name = "web", Slug = "launch-web-" + appId.ToString("N")[..8], Kind = ServiceKind.Web, SourceType = AppSourceType.Upload
        }));
        var id = Given(View(LaunchStatus.Succeeded, appId: appId, appName: "web"));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("اپت بالاست").And.Contain("هنوز آدرس عمومی ندارد");
        html.Should().NotContain("ربات اجرا شده");
    }

    [Fact]
    public async Task Problems_are_never_drawn_green_and_what_failed_comes_first()
    {
        var appId = Guid.CreateVersion7();
        var id = Given(View(LaunchStatus.SucceededWithProblems, appId: appId, publicUrl: "https://shop.apps.example.com", checks: MixedChecks));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("اپ بالا آمد، ولی یک چیز درست نیست");
        html.Should().NotContain("اپت بالاست").And.NotContain("data-launch-outcome=\"succeeded\"");

        var heading = html[html.IndexOf("data-launch-outcome", StringComparison.Ordinal)..html.IndexOf("data-launch-checks", StringComparison.Ordinal)];
        heading.Should().NotContain("text-ok");

        var failed = html.IndexOf("data-check=\"logs\"", StringComparison.Ordinal);
        var skipped = html.IndexOf("data-check=\"url\"", StringComparison.Ordinal);
        var passed = html.IndexOf("data-check=\"running\"", StringComparison.Ordinal);
        failed.Should().BeLessThan(skipped);
        skipped.Should().BeLessThan(passed);

        CheckRow(html, "logs").Should().Contain("text-danger").And.NotContain("text-ok");
    }

    [Fact]
    public async Task A_failed_launch_names_the_step_says_what_exists_and_offers_a_retry()
    {
        var appId = Guid.CreateVersion7();
        IReadOnlyList<LaunchStepView> steps =
        [
            Step(LaunchStepKeys.Source, LaunchStepState.Done),
            Step(LaunchStepKeys.Services, LaunchStepState.Done, "پستگرس shop-db ساخته شد."),
            Step(LaunchStepKeys.App, LaunchStepState.Done),
            Step(LaunchStepKeys.Deploy, LaunchStepState.Failed),
            Step(LaunchStepKeys.Verify, LaunchStepState.Waiting)
        ];
        var id = Given(View(LaunchStatus.Failed, appId: appId, appName: "shop", steps: steps, canRetry: true,
            failure: "The build failed.", failureFa: "ساخت شکست خورد."));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("اینجا گیر کرد: مرحله‌ی deploy");
        html.Should().Contain("ساخت شکست خورد.");
        var exists = html[html.IndexOf("data-launch-exists", StringComparison.Ordinal)..];
        exists.Should().Contain($"href=\"/apps/details/{appId}\"").And.Contain("پستگرس shop-db ساخته شد.");
        html.Should().Contain("دوباره امتحان کن").And.Contain($"action=\"/launch/{id}/retry\"");

        html.Should().NotContain("اپت بالاست").And.NotContain("data-launch-outcome=\"succeeded\"");
        html.Should().NotContain("اپ بالا آمد");
    }

    [Fact]
    public async Task A_failure_that_cannot_be_retried_leaves_the_next_step_to_its_own_message()
    {
        var id = Given(View(LaunchStatus.Failed, appId: Guid.CreateVersion7(), canRetry: false,
            failure: "Upload a new version.", failureFa: "از صفحه‌ی اپ نسخه‌ی تازه بفرست."));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("از صفحه‌ی اپ نسخه‌ی تازه بفرست.");
        html.Should().NotContain("data-launch-retry");
    }

    [Fact]
    public async Task A_failure_before_anything_was_made_says_nothing_was()
    {
        var id = Given(View(LaunchStatus.Failed, steps: Steps(services: LaunchStepState.Waiting), failure: "No.", failureFa: "نشد."));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("هنوز چیزی ساخته نشده.");
    }

    [Fact]
    public async Task A_failed_launch_reads_in_english_too()
    {
        var id = Given(View(LaunchStatus.Failed, steps: Steps(deploy: LaunchStepState.Failed), failure: "The build failed.", failureFa: "ساخت شکست خورد."));
        var client = await Owner();
        await SpeakEnglish(client);

        var html = await PageAsync(client, $"/launch/{id}");

        html.Should().Contain("It stopped here: deploy step").And.Contain("The build failed.");
        html.Should().NotContain("ساخت شکست خورد.");
    }

    [Fact]
    public async Task Retrying_asks_the_service_and_lands_back_on_the_page()
    {
        var id = Given(View(LaunchStatus.Failed, canRetry: true));
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/retry", token);

        response.RedirectPath().Should().Be($"/launch/{id}");
        Launches.Retried.Should().Equal(id);
    }

    [Fact]
    public async Task A_refused_retry_says_why_on_the_page()
    {
        var id = Given(View(LaunchStatus.Failed, canRetry: true));
        Launches.RetryRefusals = [new ServiceRefusal("Not now.", "الان نمی‌شود.")];
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        await client.PostFormAsync($"/launch/{id}/retry", token);

        (await PageAsync(client, $"/launch/{id}")).Should().Contain("الان نمی‌شود.");
    }

    [Fact]
    public async Task Abandoning_goes_back_to_the_question()
    {
        var (view, proposal) = Confirmable();
        var id = Given(view, proposal);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom($"/launch/{id}");

        var response = await client.PostFormAsync($"/launch/{id}/abandon", token);

        response.RedirectPath().Should().Be("/launch");
        Launches.Abandoned.Should().Equal(id);
    }

    [Fact]
    public async Task Too_late_to_abandon_says_so_on_the_page()
    {
        var id = Given(View(LaunchStatus.Deploying));
        Launches.AbandonAllowed = false;
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/launch");

        var response = await client.PostFormAsync($"/launch/{id}/abandon", token);

        response.RedirectPath().Should().Be($"/launch/{id}");
        (await PageAsync(client, $"/launch/{id}")).Should().Contain("دیگر نمی‌شود بی‌خیالش شد");
    }

    [Fact]
    public async Task An_abandoned_launch_says_nothing_was_made_and_offers_the_way_back()
    {
        var id = Given(View(LaunchStatus.Abandoned));

        var html = await PageAsync(await Owner(), $"/launch/{id}");

        html.Should().Contain("چیزی ساخته نشده بود").And.Contain("href=\"/launch\"");
    }

    // ---- the entry points --------------------------------------------------------------------------

    [Fact]
    public void Launch_heads_the_deploy_group_in_both_panel_modes_for_whoever_may_create_apps()
    {
        foreach (var mode in new[] { Harbora.Domain.Identity.PanelMode.Simple, Harbora.Domain.Identity.PanelMode.Advanced })
        {
            var deploy = NavigationMap.VisibleTo(NavigationMap.All, _ => true, mode).Single(g => g.Key == "deploy");
            deploy.Items[0].Key.Should().Be("launch");
        }

        NavigationMap.VisibleTo(c => c != Harbora.Domain.Authorization.Capabilities.AppsCreate)
            .SelectMany(g => g.Items).Should().NotContain(i => i.Key == "launch");
    }

    [Fact]
    public async Task The_sidebar_offers_it_to_somebody_who_may_create_apps_with_its_own_label()
    {
        var html = await PageAsync(await Owner(), "/apps");

        html.Should().Contain("href=\"/launch\"");
        html.Should().Contain("راه انداختن اپ");
    }

    [Fact]
    public async Task The_sidebar_does_not_offer_it_to_somebody_who_may_not()
    {
        var html = await PageAsync(await Viewer(), "/settings");

        html.Should().NotContain("راه انداختن اپ");
    }

    [Fact]
    public async Task An_empty_workspace_opens_on_the_first_run_card_not_a_table_of_zeros()
    {
        var workspace = _fixture.GivenEmptyWorkspace("launch-first-run");
        var client = await _fixture.SignedInAs(workspace, "launch-first-run@example.com");

        var html = await PageAsync(client, "/");

        html.Should().Contain("data-first-run").And.Contain("اولین اپت را بیاور");
        Regex.IsMatch(html, "<a href=\"/launch\" class=\"btn-primary").Should().BeTrue("one button, aimed at /launch");
        html.Should().NotContain("class=\"stat-bar").And.NotContain("resource-table");
    }

    [Fact]
    public async Task A_workspace_with_something_in_it_keeps_its_dashboard()
    {
        var workspace = _fixture.GivenEmptyWorkspace("launch-busy");
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        _fixture.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project { Id = projectId, WorkspaceId = workspace, Name = "P", Slug = "launch-busy-p" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment { Id = environmentId, WorkspaceId = workspace, ProjectId = projectId, Name = "E", Slug = "e", IsDefault = true });
            db.Apps.Add(new App { WorkspaceId = workspace, EnvironmentId = environmentId, Name = "busy", Slug = "launch-busy-app", SourceType = AppSourceType.Upload });
        });
        var client = await _fixture.SignedInAs(workspace, "launch-busy@example.com");

        var html = await PageAsync(client, "/");

        html.Should().NotContain("data-first-run");
        html.Should().Contain("class=\"stat-bar");
    }

    [Fact]
    public async Task The_first_run_card_offers_no_button_to_somebody_who_may_not_press_it()
    {
        var workspace = _fixture.GivenEmptyWorkspace("launch-first-viewer");
        var client = await _fixture.SignedInAs(workspace, "launch-first-viewer@example.com", SystemRole.Viewer);

        var html = await PageAsync(client, "/");

        html.Should().Contain("data-first-run").And.NotContain("اولین اپت را بیاور");
        Regex.IsMatch(html, "<a href=\"/launch\"").Should().BeFalse();
    }

    [Fact]
    public async Task The_apps_list_leads_with_launch_and_keeps_the_full_form_including_when_empty()
    {
        var workspace = _fixture.GivenEmptyWorkspace("launch-apps-empty");
        var client = await _fixture.SignedInAs(workspace, "launch-apps-empty@example.com");

        var html = await PageAsync(client, "/apps");

        Regex.IsMatch(html, "<a href=\"/launch\" class=\"btn-primary").Should().BeTrue("the primary button goes to /launch");
        html.Should().Contain("href=\"/apps/create\"");

        // The empty state's own button is the one in the shared partial.
        var empty = html[html.IndexOf("resource-shell", StringComparison.Ordinal)..];
        empty.Should().Contain("<a href=\"/launch\"").And.Contain("href=\"/apps/create\"");
    }

    [Fact]
    public async Task The_full_form_offers_launch_in_one_line_at_the_top()
    {
        var html = await PageAsync(await Owner(), "/apps/create");

        html.Should().Contain("data-launch-offer");
        html.IndexOf("data-launch-offer", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("id=\"quick-start\"", StringComparison.Ordinal));
    }

    // ---- a new version of an uploaded app ----------------------------------------------------------

    private Guid GivenApp(AppSourceType source, Guid? workspace = null)
    {
        var id = Guid.CreateVersion7();
        _fixture.Seed(db => db.Apps.Add(new App
        {
            Id = id,
            WorkspaceId = workspace ?? _fixture.WorkspaceId,
            EnvironmentId = _fixture.EnvironmentId,
            Name = "uploaded",
            Slug = "launch-up-" + id.ToString("N")[^8..],
            SourceType = source
        }));
        return id;
    }

    [Fact]
    public async Task A_new_version_is_written_queued_with_its_archive_and_lands_on_the_deployment()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");
        var bytes = Zip();

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, bytes, "shop.zip", "file"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/Deployments/Details");

        var queued = _fixture.Deployments.Queued.Should().ContainSingle(r => r.AppId == appId).Subject;
        queued.SourceArchivePath.Should().StartWith(Path.Combine(_fixture.WorkDir, "_uploads"));
        (await File.ReadAllBytesAsync(queued.SourceArchivePath!)).Should().Equal(bytes);
    }

    [Theory]
    [InlineData(AppSourceType.GitRepository)]
    [InlineData(AppSourceType.PrebuiltImage)]
    public async Task An_app_that_was_not_uploaded_refuses_a_zip(AppSourceType source)
    {
        var appId = GivenApp(source);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");
        var before = UploadedFiles().Count();

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, Zip(), "shop.zip", "file"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/Apps/Details");
        _fixture.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
        UploadedFiles().Count().Should().Be(before);
    }

    [Fact]
    public async Task A_new_version_for_another_workspaces_app_is_404()
    {
        var appId = GivenApp(AppSourceType.Upload, _fixture.OtherWorkspaceId);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, Zip(), "shop.zip", "file"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _fixture.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task Somebody_who_cannot_deploy_is_refused_a_new_version()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Viewer();
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, Zip(), "shop.zip", "file"));

        response.RedirectPath().Should().Be("/account/denied");
        _fixture.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_new_version_needs_its_antiforgery_token()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Owner();

        var response = await client.PostAsync($"/apps/{appId}/upload", new MultipartFormDataContent
        {
            { new ByteArrayContent(Zip()), "file", "shop.zip" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fixture.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_new_version_can_be_past_the_form_limits_128_megabyte_default_too()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, Zip(129 * 1024 * 1024), "big.zip", "file"));

        response.RedirectPath().Should().StartWith("/Deployments/Details");
        var queued = _fixture.Deployments.Queued.Should().ContainSingle(r => r.AppId == appId).Subject;
        new FileInfo(queued.SourceArchivePath!).Length.Should().Be(129 * 1024 * 1024);
        File.Delete(queued.SourceArchivePath!);
    }

    [Fact]
    public async Task A_junk_file_is_refused_before_it_can_fail_a_build()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, "not a zip"u8.ToArray(), "shop.zip", "file"));

        response.RedirectPath().Should().StartWith("/Apps/Details");
        _fixture.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_queue_that_refuses_deletes_the_upload_and_says_why_on_the_apps_page()
    {
        var appId = GivenApp(AppSourceType.Upload);
        var client = await Owner();
        var token = await client.AntiforgeryTokenFrom("/apps");
        var before = UploadedFiles().Count();

        _fixture.Deployments.RefuseWith = "A rollback is in progress.";
        try
        {
            var response = await client.PostAsync($"/apps/{appId}/upload", Upload(token, Zip(), "shop.zip", "file"));

            response.RedirectPath().Should().StartWith("/Apps/Details");
        }
        finally
        {
            _fixture.Deployments.RefuseWith = null;
        }

        UploadedFiles().Count().Should().Be(before, "the archive of a deployment that was never queued is nobody's");
        (await PageAsync(client, $"/apps/details/{appId}")).Should().Contain("A rollback is in progress.");
    }

    [Fact]
    public async Task An_uploaded_apps_overview_has_the_form_and_no_other_app_does()
    {
        var uploaded = GivenApp(AppSourceType.Upload);
        var fromGit = GivenApp(AppSourceType.GitRepository);
        var client = await Owner();

        var html = await PageAsync(client, $"/apps/details/{uploaded}");
        html.Should().Contain("data-app-upload").And.Contain($"action=\"/apps/{uploaded}/upload\"");
        html.Should().Contain("accept=\".zip,.tar.gz,.tgz\"");

        (await PageAsync(client, $"/apps/details/{fromGit}")).Should().NotContain("data-app-upload");
    }

    [Fact]
    public async Task The_form_is_not_offered_to_somebody_who_cannot_deploy()
    {
        var uploaded = GivenApp(AppSourceType.Upload);

        (await PageAsync(await Viewer(), $"/apps/details/{uploaded}")).Should().NotContain("data-app-upload");
    }
}
