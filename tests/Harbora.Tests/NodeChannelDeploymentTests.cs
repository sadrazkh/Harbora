using FluentAssertions;
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
    public void The_installer_backfills_both_node_settings_through_the_only_when_absent_path()
    {
        var repair = ShellFunction(Installer(), "repair_env");

        // backfill_env is the only-when-absent path: it is what makes a re-run safe and what stops
        // an operator's deliberate `false` from being flipped back on by the next update.
        repair.Should().Contain("backfill_env NodeAgent__PublicUrl");
        repair.Should().Contain("backfill_env NodeAgent__TrustForwardedClientCertificate");
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
        File.Exists(Deploy("traefik", "dynamic", "node-agent.yml")).Should().BeFalse(
            "the rendered file is generated per install, not tracked");
    }

    [Fact]
    public void Rendering_the_template_leaves_no_example_com_behind()
    {
        // The templating step is a substitution in install.sh; this is that substitution, applied to
        // the same source with the same placeholder. It proves the template is fully parameterised —
        // not that install.sh runs, which nothing here can show.
        var rendered = Template().Replace("{{PANEL_DOMAIN}}", "panel.acme.test", StringComparison.Ordinal);

        rendered.Should().NotContain("example.com");
        rendered.Should().NotContain("{{", "an unsubstituted placeholder is a router that matches nothing");
        rendered.Should().Contain("Host(`panel.acme.test`) && Path(`/api/node-agent/v1/enroll`)");
        rendered.Should().Contain("Host(`panel.acme.test`) &&");
    }

    [Fact]
    public void The_installer_renders_the_template_with_the_placeholder_the_template_uses()
    {
        var installer = Installer();

        Template().Should().Contain("{{PANEL_DOMAIN}}");
        installer.Should().Contain("{{PANEL_DOMAIN}}");
        installer.Should().Contain("traefik/node-agent.yml.template");
        installer.Should().Contain("traefik/dynamic/node-agent.yml");
    }

    [Fact]
    public void Templating_does_not_weaken_what_the_router_is_for()
    {
        var rendered = Template().Replace("{{PANEL_DOMAIN}}", "panel.acme.test", StringComparison.Ordinal);

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
        var installer = Installer();

        // A named TLS option Traefik cannot build falls back to the default one — which asks for no
        // client certificate at all. Placing the router before its CA file exists would therefore
        // publish the channel unauthenticated, so the installer refuses and says so.
        installer.Should().Contain("Could not export the node CA");
        installer.Should().Contain("harbora node-ca");
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

        // A JSON refusal is the healthy answer: the endpoint is anonymous and the request carries no
        // token. A 404 means the route is not being served at all.
        verify.Should().Contain("401");
        verify.Should().Contain("404");
    }

    [Fact]
    public void The_enrolment_preflight_explains_a_404_in_both_languages()
    {
        var verify = ShellFunction(Installer(), "verify_install");
        var enrol = verify[verify.IndexOf("/api/node-agent/v1/enroll", StringComparison.Ordinal)..];

        enrol.Should().Contain("ثبت", "the installer speaks Persian first everywhere else it explains a failure");
        enrol.Should().Contain("enrollment", "and English second");
    }
}
