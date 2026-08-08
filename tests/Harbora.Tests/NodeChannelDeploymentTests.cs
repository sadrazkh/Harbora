using FluentAssertions;
using Harbora.Web.Controllers;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The chain that turns the node channel on lives almost entirely in shipped text: the installer,
/// the Traefik template it renders, the compose file that hands the panel its settings, and the
/// admin verb the template's own comment tells an operator to run. Nothing compiles any of it, and
/// this machine can execute none of it — there is no Docker daemon and no Linux host here.
///
/// <para>
/// So these tests read the artifacts and assert that the four pieces name each other correctly: the
/// installer writes the settings the panel reads, renders the template the CA file belongs beside,
/// and probes the endpoint a node calls first. That is the whole of what they prove. That a node
/// actually enrols end to end is the live-host lane's job, not this file's.
/// </para>
/// </summary>
public class NodeChannelDeploymentTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    private static string Deploy(params string[] parts) =>
        Path.Combine([RepoRoot(), "deploy", .. parts]);

    private static string Installer() => File.ReadAllText(Deploy("install.sh"));

    private static string Template() => File.ReadAllText(Deploy("traefik", "node-agent.yml.template"));

    /// <summary>
    /// The substitution <c>install.sh</c> performs, applied to the same source with the same
    /// placeholder. It proves the template is fully parameterised — not that the installer runs.
    /// </summary>
    private static string Render(string nodeDomain) =>
        Template().Replace("{{NODE_DOMAIN}}", nodeDomain, StringComparison.Ordinal);

    /// <summary>
    /// Just the <c>routers:</c> block of the rendered file. The comments above it discuss the hosts
    /// and paths this file deliberately does <em>not</em> serve, and an assertion that reads them as
    /// configuration is an assertion about prose.
    /// </summary>
    private static string RenderedRouters(string nodeDomain)
    {
        // Git may materialise YAML as CRLF on a Windows checkout. The assertion is about the
        // routers/services structure, not the platform's line-ending policy, so make both honest
        // checkouts present the same text without weakening either boundary below.
        var rendered = Render(nodeDomain).Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = rendered.IndexOf("\n  routers:\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the template should declare routers");

        var end = rendered.IndexOf("\n  services:\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "and the routers block should end at the services block");

        return rendered[start..end];
    }

    /// <summary>
    /// The body of one shell function, so an assertion about <c>repair_env</c> cannot be satisfied by
    /// a line somewhere else in the script.
    /// </summary>
    private static string ShellFunction(string script, string name)
    {
        var start = script.IndexOf($"\n{name}() {{", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"install.sh should define a {name}() function");

        var end = script.IndexOf("\n}", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"{name}() should be closed");

        return script[start..end];
    }

    // --- the two settings that switch the channel on ---

    [Fact]
    public void The_installer_backfills_the_node_settings_through_the_only_when_absent_path()
    {
        var repair = ShellFunction(Installer(), "repair_env");

        // backfill_env is the only-when-absent path: it is what makes a re-run safe and what stops
        // an operator's deliberate override from being flipped back by the next update.
        repair.Should().Contain("backfill_env NODE_DOMAIN");
        repair.Should().Contain("backfill_env NodeAgent__PublicUrl");
    }

    [Fact]
    public void The_trust_flag_is_not_written_before_the_router_that_makes_it_true()
    {
        var installer = Installer();

        // repair_env runs before `start`. A flag written there has the panel trusting an inbound
        // X-Forwarded-Tls-Client-Cert through the whole build-and-wait window with nothing
        // overwriting it — and permanently, if the router never lands.
        ShellFunction(installer, "repair_env")
            .Should().NotContain("backfill_env NodeAgent__TrustForwardedClientCertificate",
                "the flag asserts something repair_env has not put in place yet");

        var enable = ShellFunction(installer, "enable_node_channel");
        enable.Should().Contain("backfill_env NodeAgent__TrustForwardedClientCertificate");

        // And the panel has to be recreated to see it: the value is an environment variable fixed at
        // container creation, so `restart` would re-run the same container with the old environment.
        enable.Should().Contain("docker compose up -d panel");

        var flag = enable.IndexOf("backfill_env NodeAgent__TrustForwardedClientCertificate", StringComparison.Ordinal);
        enable.IndexOf("render_node_router", StringComparison.Ordinal).Should().BeLessThan(flag,
            "the router must be on disk before the flag claims it is");
    }

    [Fact]
    public void A_fresh_install_gets_the_node_settings_by_the_same_path_an_update_does()
    {
        var write = ShellFunction(Installer(), "write_env");

        // The freshly-written .env is handed to repair_env too. Without this the derived settings
        // would exist only on installs that had been upgraded — the opposite of the intent.
        var heredoc = write.IndexOf("cat > .env", StringComparison.Ordinal);
        heredoc.Should().BeGreaterThan(-1);
        write.LastIndexOf("repair_env", StringComparison.Ordinal).Should().BeGreaterThan(heredoc,
            "the fresh-install branch must end by repairing the .env it just wrote");
    }

    [Fact]
    public void The_compose_file_passes_both_node_settings_to_the_panel()
    {
        var compose = File.ReadAllText(Deploy("docker-compose.yml"));
        var panel = compose[compose.IndexOf("\n  panel:", StringComparison.Ordinal)..];

        panel.Should().Contain("NodeAgent__PublicUrl:");
        panel.Should().Contain("NodeAgent__TrustForwardedClientCertificate:");

        // An unset variable must not reach the panel as an empty string: binding "" to a bool throws
        // at options resolution, which would turn a stale .env into a panel that will not start.
        panel.Should().Contain("${NodeAgent__TrustForwardedClientCertificate:-false}",
            "the flag must fail closed when the .env predates it");
    }

    // --- the mTLS router ---

    [Fact]
    public void The_mtls_config_is_a_template_and_not_a_file_traefik_already_watches()
    {
        File.Exists(Deploy("traefik", "node-agent.yml.template")).Should().BeTrue();

        // Shipping it inside the watched directory is what made it unfixable: it named
        // panel.example.com, it pointed at a CA file nothing created, and `git reset --hard` in the
        // update path put both back on every upgrade.
        //
        // Asserted against .gitignore rather than against the filesystem: the rendered file is a
        // per-install artifact, and a developer who has ever run the render step locally has one.
        // A test that fails on their machine and nowhere else teaches people to ignore it.
        File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"))
            .Should().Contain("deploy/traefik/dynamic/node-agent.yml",
                "the rendered file is generated per install, and must not be tracked");
    }

    [Fact]
    public void Rendering_the_template_leaves_no_example_com_behind()
    {
        // The templating step is a substitution in install.sh; this is that substitution, applied to
        // the same source with the same placeholder. It proves the template is fully parameterised —
        // not that install.sh runs, which nothing here can show.
        var rendered = Render("nodes.panel.acme.test");

        rendered.Should().NotContain("example.com");
        rendered.Should().NotContain("{{", "an unsubstituted placeholder is a router that matches nothing");
        rendered.Should().Contain("Host(`nodes.panel.acme.test`) &&");
    }

    /// <summary>
    /// The Critical this file exists to keep fixed.
    ///
    /// <para>
    /// Traefik resolves TLS options per SNI host name, not per router. Two routers on one host with
    /// different options make it log "found different TLS options for routers on the same host" and
    /// fall back to the default options — which ask for no client certificate. The node then never
    /// sends its credential, <c>passTLSClientCert</c> sets no header, and the channel answers 401
    /// forever. So: every router in this file names the same host, and every one of them carries the
    /// mTLS option set.
    /// </para>
    /// </summary>
    [Fact]
    public void One_host_name_carries_one_set_of_tls_options()
    {
        var routers = RenderedRouters("nodes.panel.acme.test");

        var hosts = System.Text.RegularExpressions.Regex
            .Matches(routers, @"Host\(`([^`]+)`\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        hosts.Should().Equal(["nodes.panel.acme.test"],
            "the node channel's host is its own; sharing the panel's is what made Traefik drop the options");

        var declared = System.Text.RegularExpressions.Regex.Matches(routers, @"\n    [a-z][a-z0-9-]*:\n").Count;
        var optioned = System.Text.RegularExpressions.Regex.Matches(routers, @"options: harbora-node-mtls").Count;

        declared.Should().Be(1, "a second router on this host is a second chance to disagree about TLS options");
        optioned.Should().Be(declared, "a router here without the mTLS options is the conflict, in one file");
    }

    [Fact]
    public void Enrolment_is_not_served_on_the_host_that_demands_a_certificate()
    {
        // A node has no certificate when it enrols — that exchange is what produces one — so it
        // cannot complete a RequireAndVerifyClientCert handshake. Enrolment is served by the panel's
        // own catch-all router on the panel's host, and the response hands back PublicUrl.
        RenderedRouters("nodes.panel.acme.test").Should().NotContain("/api/node-agent/v1/enroll",
            "putting enrolment on the mTLS host would either fail the handshake or force the host " +
            "to stop requiring a certificate");
    }

    [Fact]
    public void The_node_channel_does_not_share_the_panels_host()
    {
        var installer = Installer();
        var compose = File.ReadAllText(Deploy("docker-compose.yml"));

        // The panel's Docker label claims PANEL_DOMAIN with the default TLS options. The node router
        // must therefore be somewhere else, and NODE_DOMAIN is where.
        compose.Should().Contain("traefik.http.routers.harbora.rule=Host(`${PANEL_DOMAIN}`)");
        installer.Should().Contain("backfill_env NODE_DOMAIN \"nodes.${_panel}\"");
        installer.Should().Contain("backfill_env NodeAgent__PublicUrl \"https://${_node}\"",
            "what a node is handed at enrolment must be the host the mTLS router is on");
    }

    [Fact]
    public void The_installer_renders_the_template_with_the_placeholder_the_template_uses()
    {
        var installer = Installer();

        Template().Should().Contain("{{NODE_DOMAIN}}");
        installer.Should().Contain("{{NODE_DOMAIN}}");
        installer.Should().Contain("traefik/node-agent.yml.template");
        installer.Should().Contain("traefik/dynamic/node-agent.yml");
    }

    [Fact]
    public void Templating_does_not_weaken_what_the_router_is_for()
    {
        var rendered = Render("nodes.panel.acme.test");

        // RequireAndVerifyClientCert is also what makes the forwarded header trustworthy:
        // passTLSClientCert sets the header when there is a peer certificate but does not strip an
        // inbound one when there is none. Requiring the certificate is what guarantees an overwrite.
        rendered.Should().Contain("clientAuthType: RequireAndVerifyClientCert");
        rendered.Should().Contain("pem: true", "the panel only trusts the header because Traefik overwrites it");
    }

    // --- the CA the router points at ---

    [Fact]
    public void The_ca_path_the_template_names_is_the_one_the_installer_writes()
    {
        // Traefik mounts deploy/traefik/dynamic at /dynamic (see docker-compose.yml), so the path
        // inside the container and the path on the host are two spellings of one file. They drifted
        // before: the template named /etc/traefik/dynamic, which is not mounted anywhere.
        Template().Should().Contain("/dynamic/node-ca.pem");
        Template().Should().NotContain("/etc/traefik/dynamic/node-ca.pem");

        Installer().Should().Contain("traefik/dynamic/node-ca.pem");
    }

    [Fact]
    public void The_installer_will_not_install_the_router_without_the_ca_it_verifies_against()
    {
        var enable = ShellFunction(Installer(), "enable_node_channel");

        // A named TLS option Traefik cannot build falls back to the default one — which asks for no
        // client certificate at all. Placing the router before its CA file exists would therefore
        // publish the channel unauthenticated, so the installer refuses and says so.
        enable.Should().Contain("Could not export the node CA");
        enable.Should().Contain("harbora node-ca");
    }

    [Fact]
    public void A_missing_ca_takes_an_already_rendered_router_down_with_it()
    {
        var enable = ShellFunction(Installer(), "enable_node_channel");

        // Refusing to render is only half of it. A host that rendered the router on an earlier run
        // and has since lost node-ca.pem keeps a live router whose named TLS option Traefik cannot
        // build — which is the same unauthenticated fallback, reached from the other side. The
        // orphan is moved out of the watched directory, not merely complained about.
        enable.Should().Contain("mv -f \"$(node_rendered)\" \"$(node_disabled)\"");
        enable.Should().Contain("An orphaned node router was moved aside");

        // Out of the watched directory, not renamed inside it: Traefik's file provider reads the
        // whole directory, and "disabled" has to mean Traefik cannot see it.
        Installer().Should().Contain("traefik/node-agent.yml.disabled");
        Installer().Should().NotContain("traefik/dynamic/node-agent.yml.disabled");
    }

    [Fact]
    public void The_hand_run_ca_recovery_filters_to_the_certificate()
    {
        // The automated path strips everything outside BEGIN/END because `compose run` prints lines
        // of its own. The command an operator reaches for after a failure has to do the same, or it
        // writes a Traefik parse error into the file it is meant to repair.
        var enable = ShellFunction(Installer(), "enable_node_channel");

        var recovery = enable[enable.IndexOf("Later:", StringComparison.Ordinal)..];
        recovery.Should().Contain("sed -n '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/p'");
    }

    [Fact]
    public void The_generated_node_channel_files_cannot_be_committed_by_accident()
    {
        var ignore = File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"));

        ignore.Should().Contain("deploy/traefik/dynamic/node-agent.yml");
        ignore.Should().Contain("deploy/traefik/dynamic/node-ca.pem");
    }

    // --- the admin verb the template's comment names ---

    [Fact]
    public void Node_ca_is_a_real_verb_in_the_admin_dispatch()
    {
        var commands = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "Infrastructure", "AdminCommands.cs"));

        commands.Should().Contain("\"node-ca\" =>", "the shipped Traefik comment tells operators to run it");
        commands.Should().Contain("harbora node-ca", "and the help output should admit it exists");

        // The host wrapper is how an operator actually reaches it.
        File.ReadAllText(Deploy("harbora")).Should().Contain("node-ca");
    }

    [Fact]
    public void The_exported_ca_is_a_certificate_and_nothing_else()
    {
        // stdout is redirected straight into the file Traefik reads, so a stray line or a missing
        // final newline is a Traefik that cannot build the TLS option.
        const string pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----";

        var written = AdminCommands.CaPemForRedirect(pem);

        written.Should().Be(pem + "\n");
        AdminCommands.CaPemForRedirect(pem + "\n\n").Should().Be(pem + "\n");
    }

    // --- the preflight ---

    [Fact]
    public void Verify_install_probes_the_enrolment_endpoint()
    {
        var verify = ShellFunction(Installer(), "verify_install");

        verify.Should().Contain("/api/node-agent/v1/enroll");
        verify.Should().Contain("-X POST", "a GET on that route says nothing about whether enrolment works");

        // Sliced from the enrol probe onwards, because the panel-route check above it has a 404
        // branch of its own — asserting on the whole function passed even with this probe deleted.
        var enrol = EnrolSlice(verify);
        enrol.Should().Contain("401", "a JSON refusal is the healthy answer to a request with no token");
        enrol.Should().Contain("404", "and a 404 means the route is not being served at all");
    }

    [Fact]
    public void The_enrolment_preflight_explains_a_404_in_both_languages()
    {
        var enrol = EnrolSlice(ShellFunction(Installer(), "verify_install"));

        enrol.Should().Contain("ثبت", "the installer speaks Persian first everywhere else it explains a failure");
        enrol.Should().Contain("enrollment", "and English second");
    }

    /// <summary>
    /// The preflight that can fail. The enrol probe above it cannot: the panel's own catch-all
    /// router answers <c>/enroll</c> whether or not the mTLS router was ever rendered, so a 401
    /// there proves nothing about the channel.
    /// </summary>
    [Fact]
    public void Verify_install_fails_when_the_router_is_absent_as_loudly_as_when_it_is_stale()
    {
        var verify = ShellFunction(Installer(), "verify_install");

        // Absence. This is the state a failed CA export produces, and it used to be skipped in
        // silence because the staleness check was guarded by "if the rendered file exists".
        verify.Should().Contain("[ ! -f \"$(node_rendered)\" ]");
        verify.Should().Contain("The node mTLS router was never rendered");

        // Staleness, and the settings that have to agree with it.
        verify.Should().Contain("The node router names a different host");
        verify.Should().Contain("NodeAgent__PublicUrl");

        // Both are errors, not warnings, and both are visible in the closing message.
        verify.Should().Contain("VERIFY_FAILED=1");
        ShellFunction(Installer(), "next_steps").Should().Contain("VERIFY_FAILED");
    }

    [Fact]
    public void Verify_install_proves_the_channel_refuses_a_caller_with_no_certificate()
    {
        var verify = ShellFunction(Installer(), "verify_install");

        // The node host carries one router and it requires a client certificate, so a curl without
        // one must die in the handshake. An HTTP status back means either the router did not load
        // (404) or it loaded without its TLS options — which is precisely what Traefik does when two
        // routers claim one host name.
        verify.Should().Contain("/api/node-agent/v1/channel");
        verify.Should().Contain("mTLS is enforced");
        verify.Should().Contain("WITHOUT a client certificate");
        verify.Should().Contain("two routers claim one host name");
    }

    // --- the command the panel prints ---

    [Fact]
    public void The_enrolment_command_names_the_url_enrolment_is_actually_served_on()
    {
        // --control-plane is where the node POSTs its CSR. That is the panel's own host: the mTLS
        // host demands a client certificate, and a node enrolling has none yet.
        var command = NodeInstallCommand.For("https://panel.acme.test/", "hbr_enroll_abc", "web-01");

        command.Should().Contain("--control-plane https://panel.acme.test ",
            "a trailing slash would make the agent's base URL end in a double slash");
        command.Should().Contain("--token hbr_enroll_abc");
        command.Should().Contain("--name web-01");

        NodeInstallCommand.For("https://panel.acme.test", "t", nodeName: null)
            .Should().Contain("--name <node-name>");

        // And the controller must not build it from PublicUrl, which now names the mTLS host.
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Harbora.Web", "Controllers", "NodesController.cs"))
            .Should().NotContain("_options.PublicUrl",
                "PublicUrl is the node channel's host; enrolment is served on the panel's");
    }

    // --- what the preflight could not see ---

    [Fact]
    public void The_channel_probe_waits_for_traefik_the_way_the_panel_probe_does()
    {
        // enable_node_channel wrote that router seconds ago and Traefik's file provider may not have
        // re-read it. The panel probe retries for exactly this reason; the channel probe fired once,
        // so a set-domain followed by an update — a re-render with no container restart to absorb
        // the delay — turned a slow reload into a red VERIFY_FAILED on a correct install.
        var channel = ChannelSlice(ShellFunction(Installer(), "verify_install"));

        channel.Should().Contain("for attempt in $(seq 1 12)");
        channel.Should().Contain("sleep 5");
    }

    [Fact]
    public void A_port_with_nothing_behind_it_is_not_reported_as_mtls_working()
    {
        // curl exiting non-zero with an HTTP code of 000 is what a refused handshake looks like —
        // and equally what a closed port, a stopped Traefik or a timeout looks like. The old branch
        // tested only "curl failed" and printed the green "mTLS is enforced" for all four.
        var channel = ChannelSlice(ShellFunction(Installer(), "verify_install"));

        // 35 (SSL connect error) and 56 (the peer's alert arriving after our Finished) are the two
        // ways OpenSSL reports "the server demanded a certificate we did not have".
        channel.Should().Contain("35|56)", "only a TLS-level refusal earns the green tick");

        var green = channel.IndexOf("mTLS is enforced", StringComparison.Ordinal);
        green.Should().BeGreaterThan(channel.IndexOf("35|56)", StringComparison.Ordinal),
            "the tick has to sit inside that branch, not beside it");

        // And the cases that used to reach it say what they are instead.
        channel.Should().Contain("7|28)");
        channel.Should().Contain("it is Traefik not listening");
    }

    [Fact]
    public void Verify_install_checks_the_node_hosts_certificate_and_not_only_the_panels()
    {
        // Everything above step 5 pins the name to 127.0.0.1 with --resolve and passes -k, so a host
        // with no public DNS and no certificate at all scores four green ticks. A node is stricter
        // than curl -k: ControlPlaneTls rejects a name mismatch outright and never falls back to the
        // CA it was handed at enrollment, so Traefik's default self-signed certificate — what this
        // host serves until ACME issues — makes every node enrol and then fail TLS forever.
        var ssl = SslSlice(ShellFunction(Installer(), "verify_install"));

        ssl.Should().Contain("${node_domain}", "step 5 checked only the panel's host");

        // The two negative assertions read the commands only. The comments in this step discuss
        // --resolve and -k at length — they are there to say why this step uses neither — and an
        // assertion that reads them is an assertion about prose.
        Commands(ssl).Should().NotContain("--resolve", "a certificate check that bypasses public DNS proves nothing");
        Commands(ssl).Should().NotContain("-sk", "and one that accepts any certificate proves less than nothing");

        // The two failures are told apart, because the operator's next action differs: add a DNS
        // record, or wait for / unblock ACME.
        ssl.Should().Contain("6)", "curl exit 6 is 'this name does not resolve publicly'");
        ssl.Should().Contain("51|60)", "and 60 is 'the certificate is not one a node would accept'");
        ssl.Should().Contain("VERIFY_FAILED=1", "a node channel nothing can connect to is not a warning");
    }

    [Fact]
    public void An_update_says_the_node_channel_needs_a_dns_record_of_its_own()
    {
        var installer = Installer();

        // configure_domains is the only place that ever asked for this record, and write_env calls
        // it on a fresh .env only. An existing install upgrading gets NODE_DOMAIN backfilled and the
        // router rendered, and verify_install's --resolve makes the channel check pass green with no
        // public DNS at all — so without this the operator is told nothing whatsoever.
        installer.Should().Contain("\ncheck_node_dns() {");
        ShellFunction(installer, "cmd_update").Should().Contain("check_node_dns");

        var check = ShellFunction(installer, "check_node_dns");
        check.Should().Contain("NODE_DOMAIN", "it has to check the host this install actually uses");
        check.Should().Contain("check_dns", "against public DNS, which is the thing --resolve hides");
    }

    [Fact]
    public void The_orphaned_router_the_installer_moves_aside_cannot_be_committed_by_accident()
    {
        // fetch_source updates with `git reset --hard`, which leaves untracked files alone — so a
        // node-agent.yml.disabled dropped by a failed CA export survives in the working tree for
        // ever and shows up as a dirty repository nobody can explain.
        File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"))
            .Should().Contain("deploy/traefik/node-agent.yml.disabled");
    }

    [Fact]
    public void The_documented_cost_of_an_empty_public_url_is_the_one_it_actually_has()
    {
        // NodeEnrollmentService hands the empty string back, EnrollmentService stores it, and
        // ControlChannel reads `state.ControlPlaneUrl ?? _options.ControlPlaneUrl` — which does not
        // catch an empty string. There is no fallback to the installed URL. ChannelUri("") throws.
        // ControlChannelTests executes that; these two documents have to agree with it.
        var compose = File.ReadAllText(Deploy("docker-compose.yml"));
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "node-agent", "control-plane.md"));

        foreach (var text in new[] { compose, doc })
        {
            text.Should().NotContain("falls back to whatever URL it was installed with");
            text.Should().NotContain("keeps whatever URL it was installed with");
            text.Should().Contain("UriFormatException",
                "the failure is a channel that cannot build a URL, not one that dials the wrong host");
        }
    }

    // --- the host a tenant must not be able to take ---

    [Fact]
    public void A_custom_domain_cannot_claim_a_host_the_platform_serves_itself()
    {
        // TraefikProxyEngine.RenderRouter writes `tls: certResolver:` with no `options:`. A tenant
        // route on the node channel's host is therefore a second router on that SNI name with the
        // DEFAULT TLS options — the two-routers-one-host conflict the last commit removed, rebuilt
        // from outside, on the one host where the panel trusts a client-settable header.
        var controller = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "Controllers", "AppsController.cs"));

        controller.Should().Contain("ReservedHosts.IsReserved");

        // Both places a tenant can type a host, not just the domains form: an app can be created
        // with a custom domain in the same request that creates it.
        Between(controller, "public async Task<IActionResult> AddDomain", "app.Domains.Add")
            .Should().Contain("ReservedHost", "the domains form is the obvious way in");

        Between(controller, "var host = Harbora.Infrastructure.Deployments.ServicePlan.HostFor", "app.Domains.Add")
            .Should().Contain("ReservedHost", "and app creation takes a typed domain too");
    }

    /// <summary>
    /// Just the enrolment probe: from its URL to the start of the next numbered step. Bounded at
    /// both ends on purpose — the panel-route check before it and the channel check after it both
    /// have 404 branches of their own, so an unbounded slice let this assertion pass with the
    /// enrolment probe deleted outright.
    /// </summary>
    private static string EnrolSlice(string verifyInstall)
    {
        var start = verifyInstall.IndexOf("/api/node-agent/v1/enroll", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "verify_install should probe the enrolment endpoint");

        var end = verifyInstall.IndexOf("\n  # 4)", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the enrolment step should be followed by the next one");

        return verifyInstall[start..end];
    }

    /// <summary>Step 4 alone: the router file and the mTLS probe, up to where the SSL step begins.</summary>
    private static string ChannelSlice(string verifyInstall) =>
        Between(verifyInstall, "\n  # 4)", "\n  # 5)");

    /// <summary>
    /// Step 5 alone. Bounded at the front so the <c>--resolve</c> and <c>-k</c> that steps 2–4 use
    /// deliberately cannot satisfy — or violate — an assertion about the step that must use neither.
    /// </summary>
    private static string SslSlice(string verifyInstall)
    {
        var start = verifyInstall.IndexOf("\n  # 5)", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "verify_install should end with the certificate checks");

        return verifyInstall[start..];
    }

    /// <summary>
    /// A shell fragment with its comment-only lines removed, for assertions about what the script
    /// <em>does</em>. This file's comments explain at length what the installer deliberately does
    /// not do, so a negative assertion read against them fails on the explanation.
    /// </summary>
    private static string Commands(string shell) =>
        string.Join('\n', shell.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>The text between two markers, with both ends pinned so a slice cannot silently widen.</summary>
    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"'{from}' should be present");

        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"'{to}' should follow '{from}'");

        return text[start..end];
    }
}
