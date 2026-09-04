using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 5.1 (per-app and per-service grants, HARBORA-0035): every controller action gated on a
/// resource-level capability also asks <c>ProjectAccessService</c> whether the caller reaches the
/// specific app or service, not only whether their role carries the capability at all.
///
/// <para>
/// <c>[Authorize(Policy = Capabilities.X)]</c> only answers "does this role hold X anywhere" — the
/// policy has no idea which app or service the request names. <c>ProjectAccess.Allows</c>/
/// <c>ProjectAccessService</c> is the part that knows a placement, and a scoped member is refused by
/// it only if the action's own method body actually asks. The trap this plan's own brief names: "the
/// page will simply work" for an action that forgets to ask, because the policy attribute alone
/// already let the request through.
/// </para>
///
/// <para>
/// Reads the source rather than trusting a hand-kept list, for the reason every other census in this
/// codebase gives (<c>SupportRestrictionCensusTests</c>, <c>AppAddressCensusTests</c>): a hand-kept
/// list is checked by a reviewer noticing an addition is missing from it, and a reviewer noticing is
/// exactly the step a real gap slips past. This one goes further than a whole-file scan
/// (<c>AppAddressCensusTests</c>'s own approach) because <c>AppsController.cs</c> and
/// <c>DatabasesController.cs</c> each carry dozens of gated actions in one file — a scan that only
/// asks "does this FILE mention a scope check anywhere" would keep passing the day one action among
/// many stopped calling it, as long ninety-nine neighbours in the same file still did. So this
/// extracts each gated method's own body and asks the question of that body alone.
/// </para>
///
/// <para>
/// Only nine capabilities are resource-scoped at all — <see cref="ScopedCapabilities"/> — because
/// <c>RolePermissions</c> never hands any other capability to <c>Member</c> or <c>Operator</c>, the
/// only roles <c>WorkspaceMember.ScopedToProjects</c> can be set on; an action gated on, say,
/// <c>PlatformManage</c> is Owner/Admin-only and Owner/Admin are never scoped
/// (<c>ProjectAccess.Allows</c>'s own first check), so no grant could ever narrow it and this census
/// has nothing useful to ask of it.
/// </para>
/// </summary>
public class AppScopeCensusTests
{
    /// <summary>
    /// The capabilities <c>RolePermissions</c> hands to <c>Member</c> or <c>Operator</c> — the only
    /// roles a grant can narrow — and that name a resource with a project placement (an app, a
    /// service, a route, or a backup of one). Kept in sync with <c>RolePermissions.MemberCaps</c>/
    /// <c>OperatorCaps</c> by <see cref="Every_scoped_capability_still_belongs_to_a_scopable_role"/>
    /// below, so this list cannot silently fall out of date with the real matrix.
    /// </summary>
    private static readonly string[] ScopedCapabilities =
    [
        "AppsCreate", "AppsDeploy", "AppsOperate", "AppsDelete", "AppsEnv",
        "DatabasesManage", "RoutesManage", "GitManage", "BackupsRun"
    ];

    /// <summary>
    /// Markers that count as "this method asked". Every one of them ultimately calls
    /// <c>ProjectAccessService.CallerAsync</c> and so consults the caller's grants — <c>Guard(</c> is
    /// <c>DatabasesController</c>'s own private wrapper around <c>CanTouchServiceAsync</c>,
    /// <c>MayAsync</c>/<c>OwnsAsync</c> are the same wrapper under the name <c>AppsController</c> and
    /// <c>FunctionsController</c>/<c>BackupsController</c> use for theirs, <c>OwnsTargetAsync</c> is
    /// <c>BackupsController</c>'s own (dispatches by <c>BackupType</c> to <c>CanTouchAppAsync</c> or
    /// <c>CanTouchServiceAsync</c>), and <c>BuildMoveAsync</c> is <c>NetworksController</c>'s shared
    /// builder for its confirm/apply pair — each verified by reading its own body before being
    /// trusted here, not guessed from the name. The
    /// <c>VisibleProjectIdsAsync</c>/<c>Granted{App,Service}IdsAsync</c> trio covers a batch action
    /// that filters a whole set down to what the caller reaches rather than asking about one id.
    /// </summary>
    private static readonly Regex ScopeCheck = new(
        @"Can(Touch|See)(App|Service|Backup|Route)Async\s*\(|AllowsAsync\s*\(|MayAsync\s*\(|" +
        @"OwnsAsync\s*\(|OwnsTargetAsync\s*\(|BuildMoveAsync\s*\(|TouchableAppIdsAsync\s*\(|" +
        @"\bGuard\s*\(|VisibleProjectIdsAsync\s*\(|Granted(App|Service)IdsAsync\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Finds every <c>[Authorize(Policy = Capabilities.X)]</c> for a scoped capability, together with
    /// the method it decorates and that method's own body — read straight from
    /// <c>src/Harbora.Web/Controllers/*.cs</c>, not from reflection: the attribute and the code that
    /// answers for it live in the same file, and reading both from there is what lets this walk keep
    /// up with a file nobody remembered to update here.
    /// </summary>
    private static readonly Regex AuthorizeAttribute = new(
        @"\[Authorize\(Policy\s*=\s*Capabilities\.(?<cap>\w+)\)\]", RegexOptions.Compiled);

    /// <summary>
    /// The method signature immediately following an attribute block — allows for
    /// <c>[HttpPost(...)]</c>/<c>[ValidateAntiForgeryToken]</c>/blank lines/comments in between, the
    /// way every action in this codebase actually orders its attributes.
    /// </summary>
    private static readonly Regex MethodSignature = new(
        @"(?:public|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]*>)?|IActionResult|void)\s+" +
        @"(?<name>\w+)\s*\((?<parms>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// A gated action that does not need this census's own check, each with the reason. Keyed
    /// <c>"File.MethodName(first-parameter-or-shape)"</c> — enough to tell two overloads apart (an
    /// app's <c>Create</c> GET only renders a blank form; its POST is the one that creates something)
    /// without being brittle against an unrelated parameter rename elsewhere in the signature.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["AppsController.cs:Create(Guid? environmentId"] =
            "The GET: renders a blank creation form with no project or environment chosen yet, the "
            + "same reason ProjectsController's own Create GET needs nothing — there is no resource "
            + "or placement to ask about until the POST, which does check (AllowsAsync).",

        ["ConfigGroupsController.cs:Create(string name"] =
            "Config groups are workspace-wide shared variable catalogues (this controller's own "
            + "opening remark) — never tied to one project, app or environment, so there is no "
            + "placement for a grant to narrow. Attaching one TO an app is the act with a placement, "
            + "and that happens in AppsController.ConfigGroups.cs, which does check.",
        ["ConfigGroupsController.cs:Delete(Guid id"] = "Same reason as Create above.",
        ["ConfigGroupsController.cs:AddEntry(Guid id"] = "Same reason as Create above.",
        ["ConfigGroupsController.cs:DeleteEntry(Guid id"] = "Same reason as Create above.",

        ["DomainsController.Dns.cs:SaveDnsToken(string? token"] =
            "The workspace's own single bring-your-own Cloudflare token (this file's own opening "
            + "remark) — one per workspace, not attached to any project/app, so there is nothing for "
            + "a grant to narrow.",
        ["DomainsController.Dns.cs:RemoveDnsToken(CancellationToken ct"] = "Same reason as SaveDnsToken above.",
        ["DomainsController.Dns.cs:CreateDnsRecord(string zone"] = "Same reason as SaveDnsToken above.",
        ["DomainsController.Dns.cs:DeleteDnsRecord(string zone"] = "Same reason as SaveDnsToken above.",

        ["GitController.cs:Connect(string name"] =
            "Git provider connections and repository imports are a workspace-wide catalogue — a "
            + "GitRepository belongs to no project until an app is created FROM it, which is where "
            + "AppsController.Create's own placement check applies. There is nothing for a grant to "
            + "narrow before that point.",
        ["GitController.cs:ImportRepo(Guid providerId"] = "Same reason as Connect above.",
        ["GitController.cs:SaveOAuthConfig(GitProviderType type"] = "Same reason as Connect above.",
        ["GitController.cs:OAuthStart(GitProviderType type"] = "Same reason as Connect above.",
        ["GitController.cs:RotateSecret(Guid id"] = "Same reason as Connect above — rotates one repository's own webhook secret, not an app's.",

        ["MailController.cs:CreateDomain(string domain"] =
            "MailDomain/Mailbox are workspace-wide managed mail infrastructure with no ProjectId "
            + "column at all — the same class of resource as GitController's providers above. "
            + "DatabasesManage is reused here as the closest existing capability rather than a "
            + "purpose-built one; scoping mail by project would need a schema change out of this "
            + "plan's scope (per-app/per-service grants, not per-mail-domain ones).",
        ["MailController.cs:ConnectExternalDomain(string domain"] = "Same reason as CreateDomain above.",
        ["MailController.cs:CreateMailbox(Guid domainId"] = "Same reason as CreateDomain above.",
        ["MailController.cs:ResetPassword(Guid id"] = "Same reason as CreateDomain above.",
        ["MailController.cs:DeleteMailbox(Guid id"] = "Same reason as CreateDomain above.",
        ["MailController.cs:DeleteDomain(Guid id"] = "Same reason as CreateDomain above.",
        ["MailController.cs:RefreshDns(Guid id"] = "Same reason as CreateDomain above.",

        ["RegistryCredentialsController.cs:Create(string registryHost"] =
            "Registry credentials are a per-workspace catalogue matched to an image by registry host "
            + "at deploy time (this file's own opening remark), never tied to one project or app — "
            + "the same class of resource as GitController's providers.",
        ["RegistryCredentialsController.cs:Update(Guid id"] = "Same reason as Create above.",
        ["RegistryCredentialsController.cs:Delete(Guid id"] = "Same reason as Create above.",

        ["DatabasesController.cs:Create(ManagedServiceType? type"] =
            "The GET: renders a blank creation form with no project or environment chosen yet, the "
            + "same reason AppsController's own Create GET needs nothing — the POST overload is the "
            + "one that checks (AllowsAsync).",

        ["EmailProvidersController.cs:Create(string name"] =
            "BYO SMTP providers are a per-workspace credential catalogue (this file's own opening "
            + "remark), matched to an app only through Attach/Detach — both of which do check "
            + "(CanTouchAppAsync). Never tied to one project until attached.",
        ["EmailProvidersController.cs:Update(Guid id"] = "Same reason as Create above.",
        ["EmailProvidersController.cs:Delete(Guid id"] = "Same reason as Create above.",
        ["EmailProvidersController.cs:TestSend(Guid id"] = "Same reason as Create above — sends a test email through the provider itself, not through any app.",

        ["ErrorTrackingProvidersController.cs:Create(string name"] =
            "BYO Sentry/GlitchTip DSNs are a per-workspace credential catalogue mirroring "
            + "EmailProvidersController exactly (this file's own opening remark) — same reason, same "
            + "exemption.",
        ["ErrorTrackingProvidersController.cs:Update(Guid id"] = "Same reason as Create above.",
        ["ErrorTrackingProvidersController.cs:Delete(Guid id"] = "Same reason as Create above.",

        ["StorageController.cs:Create(string name"] =
            "Object storage buckets are a per-workspace catalogue (StorageBucket carries no "
            + "ProjectId), matched to an app only through Attach/Detach — both of which do check "
            + "(CanTouchAppAsync).",
        ["StorageController.cs:Delete(Guid id"] = "Same reason as Create above.",
        ["StorageController.cs:Measure(Guid id"] = "Same reason as Create above — asks the storage server how full the bucket itself is, not app-specific.",
        ["StorageController.cs:UploadObject(Guid id"] = "Same reason as Create above — an object lives in the bucket's own namespace, not in any one app.",
        ["StorageController.cs:DeleteObject(Guid id"] = "Same reason as Create above."
    };

    private static IEnumerable<string> ControllerFiles() =>
        Directory.EnumerateFiles(Path.Combine(TestPaths.WebRoot, "Controllers"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Skips over string/char literals and comments while counting braces, so a route template like
    /// <c>"apps/{id:guid}/logs"</c> or an interpolated <c>$"...{x}..."</c> sitting inside a method body
    /// cannot desynchronise a naive brace count. Returns the index just past the body's closing
    /// <c>}</c>, or -1 if the text ends first (a malformed extract, which callers treat as "found
    /// nothing" rather than guessing).
    /// </summary>
    private static int FindMethodBodyEnd(string text, int openBraceIndex)
    {
        var depth = 0;
        var i = openBraceIndex;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                var quote = c;
                var verbatim = quote == '"' && i > 0 && text[i - 1] == '@';
                i++;
                while (i < text.Length)
                {
                    if (verbatim)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                            i++; break;
                        }
                        i++;
                    }
                    else
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == quote) { i++; break; }
                        i++;
                    }
                }
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (c == '{') { depth++; i++; continue; }
            if (c == '}')
            {
                depth--;
                i++;
                if (depth == 0) return i;
                continue;
            }
            i++;
        }
        return -1;
    }

    private sealed record GatedAction(string File, string Capability, string Name, string Key, bool Scoped);

    private static IEnumerable<GatedAction> GatedActions()
    {
        foreach (var path in ControllerFiles())
        {
            var text = File.ReadAllText(path);
            var fileName = Path.GetFileName(path);

            foreach (Match attr in AuthorizeAttribute.Matches(text))
            {
                var capability = attr.Groups["cap"].Value;
                if (Array.IndexOf(ScopedCapabilities, capability) < 0) continue;

                var sig = MethodSignature.Match(text, attr.Index);
                if (!sig.Success) continue;

                var openBrace = text.IndexOf('{', sig.Index + sig.Length);
                if (openBrace < 0) continue;
                var bodyEnd = FindMethodBodyEnd(text, openBrace);
                if (bodyEnd < 0) continue;

                var body = text[openBrace..bodyEnd];
                var parms = sig.Groups["parms"].Value.Trim();
                var shape = parms.Length == 0 ? "" : parms.Split(',')[0].Trim();
                var key = $"{fileName}:{sig.Groups["name"].Value}({shape}";

                yield return new GatedAction(
                    fileName, capability, sig.Groups["name"].Value, key, ScopeCheck.IsMatch(body));
            }
        }
    }

    [Fact]
    public void The_census_actually_finds_gated_actions_to_read()
    {
        // Guards every other assertion here: an empty scan (a renamed attribute, a moved folder)
        // would pass them all.
        GatedActions().Should().HaveCountGreaterThan(80);
        GatedActions().Select(a => a.File).Distinct().Should().HaveCountGreaterThan(15);
    }

    [Fact]
    public void Every_resource_scoped_action_asks_the_placement_question_or_is_explained()
    {
        var unscoped = GatedActions()
            .Where(a => !a.Scoped && !Exempt.ContainsKey(a.Key))
            .Select(a => $"{a.Key}) [{a.Capability}]")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unscoped.Should().BeEmpty(
            "each of these is gated on a capability a scoped Member or Operator can hold, so its own "
            + "method body must ask ProjectAccessService whether the caller reaches this specific app "
            + "or service — the policy attribute alone only proves the role has the capability "
            + "somewhere, never that it has it HERE — or the action joins Exempt above with the "
            + "reason it has no placement to ask about");
    }

    [Fact]
    public void The_exempt_list_names_only_actions_that_still_exist()
    {
        var present = GatedActions().Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var key in Exempt.Keys)
            present.Should().Contain(key, $"{key} is exempted but no such gated action exists any more");
    }

    /// <summary>
    /// Guards <see cref="ScopedCapabilities"/> itself: if a future capability is ever added to
    /// <c>MemberCaps</c>/<c>OperatorCaps</c> and it names a project-placed resource, this census must
    /// start covering it rather than staying silent about a ninth or tenth capability nobody taught
    /// it. Conversely, nothing in the list may be Owner/Admin-only — those roles are never scoped, so
    /// this census would be asking a question that can never fail.
    /// </summary>
    [Fact]
    public void Every_scoped_capability_still_belongs_to_a_scopable_role()
    {
        foreach (var capability in ScopedCapabilities)
        {
            var constant = typeof(Harbora.Domain.Authorization.Capabilities)
                .GetField(capability)?.GetValue(null) as string;
            constant.Should().NotBeNull($"Capabilities.{capability} must exist");

            var allowsMember = Harbora.Domain.Authorization.RolePermissions.Allows(
                Harbora.Domain.Common.SystemRole.Member, constant!);
            var allowsOperator = Harbora.Domain.Authorization.RolePermissions.Allows(
                Harbora.Domain.Common.SystemRole.Operator, constant!);

            (allowsMember || allowsOperator).Should().BeTrue(
                $"{capability} is in ScopedCapabilities but RolePermissions never hands it to a "
                + "scopable role, so no grant could ever narrow it — remove it from the list above");
        }

        foreach (var capability in Harbora.Domain.Authorization.Capabilities.All)
        {
            var name = typeof(Harbora.Domain.Authorization.Capabilities).GetFields()
                .First(f => Equals(f.GetValue(null), capability)).Name;
            if (Array.IndexOf(ScopedCapabilities, name) >= 0) continue;

            var allowsMember = Harbora.Domain.Authorization.RolePermissions.Allows(
                Harbora.Domain.Common.SystemRole.Member, capability);
            var allowsOperator = Harbora.Domain.Authorization.RolePermissions.Allows(
                Harbora.Domain.Common.SystemRole.Operator, capability);

            (allowsMember || allowsOperator).Should().BeFalse(
                $"{name} is now handed to a scopable role but is missing from ScopedCapabilities "
                + "above — this census would silently stop covering its actions");
        }
    }
}
