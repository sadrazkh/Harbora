# Deploy App Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `harbora deploy` offers a CapRover-style list of apps, always sends the server's own spelling of the slug, and reports a rejected upload as the rejection it was.

**Architecture:** The app-selection rules move out of `DeployCommand.ExecuteAsync` into a pure `AppChoice.Resolve`, tested without a terminal or a server — the same treatment `DeployPlan.Decide` already gets. The deploy methods then take a `RemoteApp` instead of a `string slug`, so the string the user typed is structurally incapable of reaching a URL. `ProjectConfig` gains a single-line rewrite for correcting an existing `harbora.yml`, and `ApiClient.PostFileAsync` asks the server for permission before it sends 3 MB.

**Tech Stack:** .NET 10, Spectre.Console (CLI + prompts), xunit + FluentAssertions.

**Spec:** [docs/superpowers/specs/2026-08-16-deploy-app-picker-design.md](../specs/2026-08-16-deploy-app-picker-design.md)

## Global Constraints

- Target framework is `net10.0`; the CLI is `src/Harbora.Cli`, tests are flat files in `tests/Harbora.Tests`.
- Tests use xunit `[Fact]`/`[Theory]` with FluentAssertions (`.Should()`), and carry a class-level `<summary>` saying why the behaviour matters. Follow `tests/Harbora.Tests/DeployPlanTests.cs`.
- `harbora.yml` key names are a public contract (`docs/cli-deploy.md`); do not rename or reorder them.
- Nothing may block on a prompt when `Interactive.IsAvailable` is false — CI must fail with an explanation, never hang.
- The server keeps comparing slugs ordinally. Do not touch `ApiV1Controller.DeployArchive`.
- Do not pipe `dotnet build`/`dotnet test` into `head`/`tee`; a pipe hides the exit code.

---

### Task 1: `AppChoice` — the selection rules, as a pure function

**Files:**
- Create: `src/Harbora.Cli/AppChoice.cs`
- Test: `tests/Harbora.Tests/DeployAppChoiceTests.cs`

**Interfaces:**
- Consumes: `RemoteApp(string Slug, string Name, string Status, string Source, bool CanServerPull)` from `src/Harbora.Cli/Interactive.cs`.
- Produces:
  - `AppChoice.Choice(RemoteApp? Current, bool NeedsPrompt, string? Problem)` — `Current` is what the typed name resolved to (null when nothing matched); `NeedsPrompt` means the caller shows the list; `Problem` is what was wrong with the name given, printed either way, and fatal only when `NeedsPrompt` is false and `Current` is null.
  - `AppChoice.Resolve(string? typedSlug, IReadOnlyList<RemoteApp> apps, bool interactive, bool yes) → Choice`
  - `AppChoice.Order(IReadOnlyList<RemoteApp> apps, RemoteApp? current) → IReadOnlyList<RemoteApp>`

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/DeployAppChoiceTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which app a deploy is for, decided before anything is asked or uploaded.
///
/// Reported from a real session: `harbora deploy Kousar-kolie` against an app whose slug is
/// `kousar-kolie` packed 311 files, uploaded 3.1 MB, and failed with "Error while copying content to
/// a stream." The CLI matched the name case-insensitively and then sent what the user typed; the
/// server compares ordinally, answered 404 before reading the body, and tore the upload down. So the
/// rule pinned here is that resolving a name yields the *server's* spelling of it, never the
/// caller's — and that a name nobody recognises is never quietly resolved to something else.
/// </summary>
public class DeployAppChoiceTests
{
    private static RemoteApp App(string slug) => new(slug, slug, "Running", "Upload", false);

    private static readonly IReadOnlyList<RemoteApp> Two = [App("kousar-kolie"), App("subscriptionlink")];

    [Fact]
    public void A_name_resolves_to_the_servers_spelling_of_it()
    {
        var choice = AppChoice.Resolve("Kousar-kolie", Two, interactive: false, yes: true);

        choice.Current!.Slug.Should().Be("kousar-kolie");
        choice.Problem.Should().BeNull();
    }

    [Fact]
    public void A_terminal_is_offered_the_list_even_when_the_name_is_known()
    {
        // The point of the picker: `harbora deploy` shows what you could deploy to, the way CapRover
        // does, rather than silently acting on a name written into a file months ago.
        var choice = AppChoice.Resolve("kousar-kolie", Two, interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeTrue();
        choice.Current!.Slug.Should().Be("kousar-kolie", "the current app is what the list preselects");
    }

    [Fact]
    public void Yes_asks_nothing()
    {
        AppChoice.Resolve("kousar-kolie", Two, interactive: true, yes: true)
            .NeedsPrompt.Should().BeFalse();
    }

    [Fact]
    public void No_terminal_asks_nothing()
    {
        // CI must fail with an explanation rather than block on input nobody can give.
        AppChoice.Resolve("kousar-kolie", Two, interactive: false, yes: false)
            .NeedsPrompt.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_name_is_never_resolved_to_something_else()
    {
        var choice = AppChoice.Resolve("typo", Two, interactive: false, yes: true);

        choice.Current.Should().BeNull();
        choice.NeedsPrompt.Should().BeFalse();
        choice.Problem.Should().Contain("typo");
    }

    [Fact]
    public void An_unknown_name_in_a_terminal_says_so_and_still_offers_the_list()
    {
        var choice = AppChoice.Resolve("typo", Two, interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeTrue();
        choice.Current.Should().BeNull("nothing should be preselected when the name matched nothing");
        choice.Problem.Should().Contain("typo");
    }

    [Fact]
    public void No_name_and_no_terminal_explains_itself()
    {
        var choice = AppChoice.Resolve(null, Two, interactive: false, yes: false);

        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("No app specified");
    }

    [Fact]
    public void A_single_app_is_not_a_question()
    {
        // A one-item menu asks a question with no answer.
        var choice = AppChoice.Resolve(null, [App("only-one")], interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeFalse();
        choice.Current!.Slug.Should().Be("only-one");
    }

    [Fact]
    public void A_single_app_does_not_absorb_a_name_that_is_not_it()
    {
        // Deploying to the only app because the name given was wrong is the one outcome worse than
        // failing: it deploys, and to the wrong place.
        var choice = AppChoice.Resolve("something-else", [App("only-one")], interactive: false, yes: true);

        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("something-else");
    }

    [Fact]
    public void An_account_with_no_apps_is_told_where_to_make_one()
    {
        var choice = AppChoice.Resolve(null, [], interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeFalse();
        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("panel");
    }

    [Fact]
    public void The_current_app_is_offered_first()
    {
        var ordered = AppChoice.Order(Two, Two[1]);

        ordered.Select(a => a.Slug).Should().Equal("subscriptionlink", "kousar-kolie");
    }

    [Fact]
    public void Without_a_current_app_the_order_is_left_alone()
    {
        AppChoice.Order(Two, null).Should().Equal(Two);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~DeployAppChoiceTests"
```

Expected: build FAILS with `CS0103: The name 'AppChoice' does not exist in the current context`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Harbora.Cli/AppChoice.cs`:

```csharp
namespace Harbora.Cli;

/// <summary>
/// Which app a deploy is for. Kept separate from the command, and free of any prompt, so the rules
/// can be tested without a terminal — they are the part users will argue with, and getting them
/// wrong means deploying to something other than what was asked for.
///
/// The one rule everything else serves: a resolved app carries the server's spelling of its slug.
/// A name that was typed is only ever used to *find* an app, never to address one. `Kousar-kolie`
/// matched `kousar-kolie` here and was then sent to the server verbatim, which compares ordinally —
/// a 404 the CLI never showed, because it arrived while a 3.1 MB upload was still being written.
/// </summary>
public static class AppChoice
{
    /// <param name="Current">What the typed name resolved to, or null when nothing matched.</param>
    /// <param name="NeedsPrompt">Whether the caller should offer the list.</param>
    /// <param name="Problem">
    /// What was wrong with the name given. Printed either way; fatal only when there is nothing to
    /// prompt with and nothing was resolved.
    /// </param>
    public sealed record Choice(RemoteApp? Current, bool NeedsPrompt, string? Problem);

    public static Choice Resolve(
        string? typedSlug, IReadOnlyList<RemoteApp> apps, bool interactive, bool yes)
    {
        if (apps.Count == 0)
            return new(null, false, "This account has no apps yet. Create one in the panel first.");

        var typed = (typedSlug ?? "").Trim();

        // Case-insensitively, because that is how people type — but what comes back is the app, and
        // the app knows its own slug.
        var current = typed.Length == 0
            ? null
            : apps.FirstOrDefault(a => a.Slug.Equals(typed, StringComparison.OrdinalIgnoreCase));

        var unknown = typed.Length > 0 && current is null;
        var problem = unknown
            ? $"No app called {typed} on this account."
            : null;

        var canAsk = interactive && !yes;

        // A single app is not a question — unless a name was given and it was not that one, in which
        // case answering the question nobody asked would deploy to the wrong app.
        if (!unknown && apps.Count == 1) return new(apps[0], false, null);

        if (canAsk) return new(current, true, problem);
        if (current is not null) return new(current, false, null);

        return new(null, false,
            problem ?? "No app specified — pass one, or add app: to harbora.yml.");
    }

    /// <summary>
    /// The apps in the order they should be offered. Spectre's <c>SelectionPrompt</c> cannot
    /// pre-highlight a choice, so position carries that meaning: the current app is first, and
    /// pressing Enter accepts it.
    /// </summary>
    public static IReadOnlyList<RemoteApp> Order(IReadOnlyList<RemoteApp> apps, RemoteApp? current) =>
        current is null ? apps : [current, .. apps.Where(a => !ReferenceEquals(a, current))];
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~DeployAppChoiceTests"
```

Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Cli/AppChoice.cs tests/Harbora.Tests/DeployAppChoiceTests.cs
git commit -m "A resolved app carries the server's spelling of its slug"
```

---

### Task 2: Correcting the app name in an existing `harbora.yml`

**Files:**
- Modify: `src/Harbora.Cli/ProjectConfig.cs` (add a method; leave `Parse` alone)
- Test: `tests/Harbora.Tests/ProjectConfigRewriteTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig.StripComment` (private, already in the file), `ProjectConfig.Load`.
- Produces: `ProjectConfig.RewriteAppSlug(string path, string slug)` — replaces the value of the top-level `app:`/`name:` line in the file at `path`, appending `app: <slug>` when there is none. Every other line is written back byte-for-byte.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/ProjectConfigRewriteTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Correcting the app name in a config the user already has.
///
/// The first `harbora deploy Kousar-kolie` wrote that name into harbora.yml, and RememberApp never
/// overwrites — so the folder repeated the same hidden 404 on every later run, with no way out short
/// of editing the file by hand. Fixing it must not cost the user the rest of their config, so this
/// replaces one line rather than regenerating a file from the two fields the CLI happens to know.
/// </summary>
public class ProjectConfigRewriteTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void The_app_name_is_replaced_and_everything_else_survives()
    {
        var path = WriteTemp(
            """
            # Written by `harbora deploy`. Full schema: docs/cli-deploy.md
            app: Kousar-kolie
            server: https://platform.irnetfree.info
            dockerfile: docker/Dockerfile
            ignore:
              - node_modules
              - .cache
            """);

        ProjectConfig.RewriteAppSlug(path, "kousar-kolie");

        var config = ProjectConfig.Parse(File.ReadAllLines(path));
        config.App.Should().Be("kousar-kolie");
        config.Server.Should().Be("https://platform.irnetfree.info");
        config.Dockerfile.Should().Be("docker/Dockerfile");
        config.Ignore.Should().Equal("node_modules", ".cache");
        File.ReadAllText(path).Should().Contain("# Written by", "comments are the user's, not ours");

        File.Delete(path);
    }

    [Fact]
    public void The_name_alias_is_rewritten_where_it_stands()
    {
        // `name:` is the documented alias for `app:`. Appending a second key would leave the file
        // saying two different things, and Parse takes the last one it reads.
        var path = WriteTemp("name: old-app\nserver: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "new-app");

        File.ReadAllText(path).Should().NotContain("old-app");
        ProjectConfig.Parse(File.ReadAllLines(path)).App.Should().Be("new-app");

        File.Delete(path);
    }

    [Fact]
    public void A_config_with_no_app_line_gains_one()
    {
        var path = WriteTemp("server: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "my-api");

        var config = ProjectConfig.Parse(File.ReadAllLines(path));
        config.App.Should().Be("my-api");
        config.Server.Should().Be("https://panel.example.com");

        File.Delete(path);
    }

    [Fact]
    public void An_indented_app_key_is_not_the_app_name()
    {
        // `app:` nested under another key belongs to that block. Rewriting it would corrupt the file
        // and leave the real app name untouched.
        var path = WriteTemp("build:\n  app: inner\nserver: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "my-api");

        var text = File.ReadAllText(path);
        text.Should().Contain("  app: inner");
        ProjectConfig.Parse(File.ReadAllLines(path)).App.Should().Be("my-api");

        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~ProjectConfigRewriteTests"
```

Expected: build FAILS with `CS0117: 'ProjectConfig' does not contain a definition for 'RewriteAppSlug'`.

- [ ] **Step 3: Write minimal implementation**

In `src/Harbora.Cli/ProjectConfig.cs`, add this method directly after `Parse` (before `StripComment`):

```csharp
    /// <summary>
    /// Points an existing config at a different app, by replacing the value on the one line that
    /// names it. Deliberately not a regeneration: a project that has grown <c>dockerfile:</c>,
    /// <c>context:</c>, <c>ignore:</c> or <c>dockerfileLines:</c> must not lose them to a file the
    /// CLI rebuilt from the two fields it happens to care about.
    ///
    /// A trailing comment on the app line does not survive; nothing else is touched.
    /// </summary>
    public static void RewriteAppSlug(string path, string slug)
    {
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;   // nested: not the app name

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (line[..colon].Trim().ToLowerInvariant() is not ("app" or "name")) continue;

            // Keep the key exactly as written — `name:` is the documented alias, and a file that
            // uses it should keep using it.
            lines[i] = $"{line[..colon]}: {slug}";
            File.WriteAllLines(path, lines);
            return;
        }

        File.WriteAllLines(path, [.. lines, $"app: {slug}"]);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~ProjectConfigRewriteTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Cli/ProjectConfig.cs tests/Harbora.Tests/ProjectConfigRewriteTests.cs
git commit -m "Point an existing harbora.yml at a different app without rewriting it"
```

---

### Task 3: An upload that reports the server's answer

**Files:**
- Modify: `src/Harbora.Cli/ApiClient.cs:51-61` (`PostFileAsync`)
- Test: `tests/Harbora.Tests/ArchiveUploadTests.cs`

**Interfaces:**
- Consumes: `ApiClient(string server, string? token, HttpMessageHandler handler)` — the handler-injecting constructor that already exists for exactly this.
- Produces: no signature change. `PostFileAsync` sets `Expect: 100-continue` and rethrows a transport failure with text naming the request.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/ArchiveUploadTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the CLI tells you when an upload is refused.
///
/// `DeployArchive` answers 404 or 403 before it reads a byte of the body. The connection is then torn
/// down while the CLI is still writing megabytes into it, the write fails, and HttpClient reports the
/// transport error — "Error while copying content to a stream." — with the real status discarded.
/// Two runs of a real deploy failed that way and named nothing. So: ask before sending, and if the
/// send still fails, say which request failed rather than stating a fact about a stream.
/// </summary>
public class ArchiveUploadTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(reply(request));
        }
    }

    private static string TempArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.tar.gz");
        File.WriteAllBytes(path, [0x1f, 0x8b, 0x08, 0x00]);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task The_server_is_asked_before_the_body_is_sent()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, """{"deploymentId":"abc"}"""));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        await api.PostFileAsync("apps/kousar-kolie/deploy/archive", archive);

        stub.Seen!.Headers.ExpectContinue.Should().BeTrue(
            "without it the server cannot refuse until megabytes are already on the wire");
        stub.Seen.RequestUri!.AbsolutePath.Should().Be("/api/v1/apps/kousar-kolie/deploy/archive");

        File.Delete(archive);
    }

    [Fact]
    public async Task A_refused_upload_reports_what_the_server_said()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"App not found."}"""));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        var act = () => api.PostFileAsync("apps/Kousar-kolie/deploy/archive", archive);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("App not found");

        File.Delete(archive);
    }

    [Fact]
    public async Task A_connection_torn_down_mid_upload_names_the_request()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("Error while copying content to a stream."));
        var api = new ApiClient("https://panel.example.com", "tok", stub);
        var archive = TempArchive();

        var act = () => api.PostFileAsync("apps/kousar-kolie/deploy/archive", archive);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().Contain("apps/kousar-kolie/deploy/archive");
        thrown.InnerException.Should().NotBeNull("the transport detail is still worth keeping");

        File.Delete(archive);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~ArchiveUploadTests"
```

Expected: `The_server_is_asked_before_the_body_is_sent` FAILS — "Expected ExpectContinue to be True, but found <null>"; `A_connection_torn_down_mid_upload_names_the_request` FAILS on the message assertion.

- [ ] **Step 3: Write minimal implementation**

Replace the body of `PostFileAsync` in `src/Harbora.Cli/ApiClient.cs`:

```csharp
    /// <summary>
    /// Streams a file as the raw request body. Used to push a packed project without loading it into
    /// memory — a source tree can be tens of megabytes.
    /// </summary>
    public async Task<JsonElement> PostFileAsync(string path, string filePath)
    {
        await using var file = File.OpenRead(filePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        // Uploading + building can take minutes; the default 100s timeout would cut it short.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + path) { Content = content };

        // Let the server refuse before the body is on the wire. The archive endpoint answers 404 or
        // 403 without reading the body, and without this the rejection arrives mid-upload: the write
        // fails, and the caller is told about a stream instead of about an app name.
        request.Headers.ExpectContinue = true;

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        }
        catch (HttpRequestException ex)
        {
            // Expect: is a courtesy, not a guarantee — a proxy may drop it, and the server may still
            // reject mid-body. Name the request that failed; "Error while copying content to a
            // stream." on its own sent a real deploy round twice with nothing to go on.
            throw new HttpRequestException(
                $"Upload to {path} was cut off by the server — most often the app name is not one the "
                + $"server recognises, or the token cannot deploy. ({ex.Message})", ex);
        }

        return await ReadAsync(res);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~ArchiveUploadTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Cli/ApiClient.cs tests/Harbora.Tests/ArchiveUploadTests.cs
git commit -m "A refused upload reports the refusal, not the stream"
```

---

### Task 4: Wire it into `harbora deploy`

**Files:**
- Modify: `src/Harbora.Cli/Commands.cs:165-383` (`DeployCommand`)
- Modify: `src/Harbora.Cli/Interactive.cs:73-88` (`ChooseApp`), and add `OfferSlugUpdate`

**Interfaces:**
- Consumes: `AppChoice.Resolve`, `AppChoice.Order`, `ProjectConfig.RewriteAppSlug` from Tasks 1–2.
- Produces:
  - `Interactive.ChooseApp(IReadOnlyList<RemoteApp> apps, RemoteApp? current = null) → RemoteApp?`
  - `Interactive.OfferSlugUpdate(string dir, string slug) → void`
  - `DeployCommand.Settings.Yes` — `-y|--yes`
  - The private deploy helpers take `RemoteApp app` where they took `string slug`.

- [ ] **Step 1: Add the `--yes` flag**

In `src/Harbora.Cli/Commands.cs`, after the `--push` option (line 185-186):

```csharp
        [CommandOption("-y|--yes"), Description("Don't ask which app — use the one already configured")]
        public bool Yes { get; init; }
```

- [ ] **Step 2: Teach the picker about the current app**

Replace `ChooseApp` in `src/Harbora.Cli/Interactive.cs`:

```csharp
    /// <summary>
    /// Which app to deploy. Offered on every interactive deploy, the way CapRover does it, rather
    /// than only when nothing named one — a name written into a file months ago is exactly the thing
    /// worth showing somebody before 3 MB goes to it.
    /// </summary>
    public static RemoteApp? ChooseApp(IReadOnlyList<RemoteApp> apps, RemoteApp? current = null)
    {
        if (apps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]![/] This account has no apps yet. Create one in the panel first.");
            return null;
        }
        if (apps.Count == 1) return apps[0];

        return AnsiConsole.Prompt(
            new SelectionPrompt<RemoteApp>()
                .Title("Which app do you want to deploy?")
                .PageSize(15)
                .UseConverter(a => ReferenceEquals(a, current)
                    ? $"{a.Slug} [grey]({a.Name} · {a.Status})[/] [green](current)[/]"
                    : $"{a.Slug} [grey]({a.Name} · {a.Status})[/]")
                .AddChoices(AppChoice.Order(apps, current)));
    }
```

- [ ] **Step 3: Add the confirmed config correction**

Add to `src/Harbora.Cli/Interactive.cs`, after `RememberApp`:

```csharp
    /// <summary>
    /// Offers to point an existing <c>harbora.yml</c> at the app that was actually chosen.
    ///
    /// RememberApp deliberately never overwrites, which is right for a decision the project has
    /// already made — but it is how a wrong name became permanent: the first deploy wrote
    /// <c>app: Kousar-kolie</c>, the server only answers to <c>kousar-kolie</c>, and every later run
    /// in that folder repeated the same hidden 404. Asking is the difference between the two cases.
    /// </summary>
    public static void OfferSlugUpdate(string dir, string slug)
    {
        var path = ProjectConfig.Locate(dir);
        if (path is null || !IsAvailable) return;

        var existing = ProjectConfig.Load(dir).App;
        if (string.Equals(existing, slug, StringComparison.Ordinal)) return;

        var file = Path.GetFileName(path);
        var was = string.IsNullOrWhiteSpace(existing) ? "no app" : existing!;
        if (!AnsiConsole.Confirm(
                $"{file} says [yellow]{Markup.Escape(was)}[/]. Update it to [green]{Markup.Escape(slug)}[/]?"))
            return;

        try
        {
            ProjectConfig.RewriteAppSlug(path, slug);
            AnsiConsole.MarkupLine($"[grey]Updated {file}[/]");
        }
        catch (Exception ex)
        {
            // Not being able to save the answer is not a reason to fail a deploy that worked.
            AnsiConsole.MarkupLine($"[grey]Could not update {file}: {Markup.Escape(ex.Message)}[/]");
        }
    }
```

`Interactive.cs` needs `using Spectre.Console;` — it already has it at line 2.

- [ ] **Step 4: Replace the selection block in `ExecuteAsync`**

In `src/Harbora.Cli/Commands.cs`, replace everything from `if (string.IsNullOrWhiteSpace(slug))` (line 244) through the closing brace of the `if (app is null)` block (line 281) with:

```csharp
        var choice = AppChoice.Resolve(slug, apps, Interactive.IsAvailable, settings.Yes);
        if (choice.Problem is not null)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(choice.Problem)}");

        var app = choice.NeedsPrompt ? Interactive.ChooseApp(apps, choice.Current) : choice.Current;
        if (app is null)
        {
            if (apps.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[grey]Available:[/] {Markup.Escape(string.Join(", ", apps.Select(a => a.Slug)))}");
            return 1;
        }

        // From here the app is the only thing that names itself. The string the user typed matched an
        // app case-insensitively; the server compares ordinally, so sending it back is a 404 — and one
        // that arrives mid-upload, where it reads as a broken stream.
        slug = app.Slug;
```

- [ ] **Step 5: Make the typed string incapable of reaching a URL**

Still in `src/Harbora.Cli/Commands.cs`, change the four deploy helpers to take the app rather than a slug. Replace the dispatch block (lines 298-314 in the original) with:

```csharp
        string? deploymentId;
        try
        {
            deploymentId = plan.Mode switch
            {
                DeployMode.Image        => await DeployImageAsync(api, app, plan.Value!),
                DeployMode.PushTarball  => await UploadAsync(api, app, plan.Value!, deleteAfter: false),
                DeployMode.PushGitBranch => await PushBranchAsync(api, app, dir, plan.Value!, config, ct),
                DeployMode.PushFolder   => await PushFolderAsync(api, app, dir, config, ct),
                _                       => await TriggerAsync(api, app, plan.Value)
            };
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
```

and change the five helper signatures and their URL construction:

```csharp
    private static async Task<string?> TriggerAsync(ApiClient api, RemoteApp app, string? gitRef)
    {
        var res = await api.PostAsync($"apps/{app.Slug}/deploy", new { gitRef });
        return res.GetProperty("deploymentId").GetString();
    }

    private static async Task<string?> DeployImageAsync(ApiClient api, RemoteApp app, string image)
    {
        AnsiConsole.MarkupLine($"[grey]Releasing image[/] {image}");
        var res = await api.PostAsync($"apps/{app.Slug}/deploy", new { image });
        return res.GetProperty("deploymentId").GetString();
    }
```

`PushFolderAsync(ApiClient api, RemoteApp app, string dir, ProjectConfig config, CancellationToken ct)` and
`PushBranchAsync(ApiClient api, RemoteApp app, string dir, string branch, ProjectConfig config, CancellationToken ct)`
change only their second parameter and pass `app` to `UploadAsync`:

```csharp
    private static async Task<string?> UploadAsync(ApiClient api, RemoteApp app, string archivePath, bool deleteAfter)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException($"Archive not found: {archivePath}");
        try
        {
            AnsiConsole.MarkupLine($"[grey]Uploading[/] {new FileInfo(archivePath).Length / 1024.0 / 1024:0.#} MB…");
            var res = await api.PostFileAsync($"apps/{app.Slug}/deploy/archive", archivePath);
            return res.GetProperty("deploymentId").GetString();
        }
        finally
        {
            if (deleteAfter) { try { File.Delete(archivePath); } catch { /* temp file */ } }
        }
    }
```

- [ ] **Step 6: Offer the config correction**

Replace the `RememberApp` block (lines 291-293 in the original) with:

```csharp
        // Save the answer so this folder never has to be asked — or told — again. RememberApp never
        // overwrites: a project that already has a config has already decided. When it does have one
        // and the choice differs, ask rather than leaving a name the server does not answer to.
        if (Interactive.RememberApp(dir, slug!, api.Server))
            AnsiConsole.MarkupLine(
                $"[grey]Wrote {ProjectConfig.DefaultFileName} — next time just run[/] harbora deploy");
        else if (!settings.Yes)
            Interactive.OfferSlugUpdate(dir, slug!);
```

- [ ] **Step 7: Build and run the whole CLI test set**

```bash
dotnet build src/Harbora.Cli/Harbora.Cli.csproj
```

Expected: `Build succeeded`, 0 errors.

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~Cli|FullyQualifiedName~Deploy|FullyQualifiedName~ProjectConfig|FullyQualifiedName~ArchiveUpload|FullyQualifiedName~SourcePacker|FullyQualifiedName~SelfUpdate"
```

Expected: PASS, no failures.

- [ ] **Step 8: Commit**

```bash
git add src/Harbora.Cli/Commands.cs src/Harbora.Cli/Interactive.cs
git commit -m "harbora deploy shows the apps, and addresses the one it was given"
```

---

### Task 5: Documentation, and the binary the owner actually runs

**Files:**
- Modify: `docs/cli-deploy.md:78-99` (the deploy section and the mode table)
- Modify: `README.md` — only if the `harbora deploy` block there describes the picker

- [ ] **Step 1: Document the picker and `--yes`**

In `docs/cli-deploy.md`, replace the paragraph at lines 84-85 with:

```markdown
Every interactive deploy shows your apps and asks which one, the way CapRover does. The app from
`harbora.yml` (or the command line) is listed **first** and marked `(current)`, so pressing Enter
deploys where you deployed last. Pick a different one and the CLI offers to update `harbora.yml`.

Pass `--yes` to skip the question, and it is skipped automatically when there is no terminal — so CI
behaves exactly as before. With no `harbora.yml` and no app name, the CLI writes `harbora.yml` after
you choose, so the next run needs nothing. `harbora init` still writes the fuller commented file.
```

Add to the mode table after the `--push` row:

```markdown
| `harbora deploy --yes` | Uses the configured app without asking (implied in CI) |
```

- [ ] **Step 2: Run the documentation drift guard**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~DocumentationDriftTests"
```

Expected: PASS. This suite asserts that every registered command appears in `README.md` and that no
document claims a CLI command count the CLI does not have. No command was added, so it should be
green; if it is not, fix the document it names.

- [ ] **Step 3: Run the full test suite**

```bash
dotnet test Harbora.slnx
```

Expected: PASS. Some suites need Docker and are skipped on this machine — note any skips rather than
treating them as failures, and do not report success if anything actually failed.

- [ ] **Step 4: Commit**

```bash
git add docs/cli-deploy.md README.md
git commit -m "Document the app picker and --yes"
```

- [ ] **Step 5: Rebuild and reinstall the CLI the owner runs**

The failing binary is the installed `0.2.0` at `C:\Users\sadra\AppData\Local\Harbora\harbora`, not the
checkout. Nothing above changes the owner's experience until it is replaced.

```bash
dotnet publish src/Harbora.Cli/Harbora.Cli.csproj -c Release -r win-x64 --self-contained false -o /tmp/harbora-cli
```

Then copy the produced `harbora.exe` over `C:\Users\sadra\AppData\Local\Harbora\harbora.exe`.
**Ask the owner before overwriting it** — it is the tool they deploy with, and a half-copied binary
leaves them with none.

- [ ] **Step 6: Verify against the deploy that failed**

The acceptance test is the deploy that has failed twice. From
`E:\Cash.Net\source\vsCode\HamrahKolie`:

```bash
harbora deploy
```

Expected: a list showing `Kousar-kolie (current)` and `subscriptionlink`; choosing `kousar-kolie`
offers to update `harbora.yml`; the upload is accepted and a deployment id is printed. If it is
refused, the message now names the app or the token — report that message rather than retrying.
