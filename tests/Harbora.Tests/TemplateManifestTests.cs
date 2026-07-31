using FluentAssertions;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Reading a template manifest, and refusing one that cannot work.
///
/// The manifests always described more than the platform used, and nothing said so: a static-site
/// template declared a volume for its content and an app created from it got none, so the site was
/// empty again after every redeploy. The manifest was documentation dressed as configuration. It is
/// also checked where someone writes it, because a deploy failing an hour later is the expensive
/// moment to find a typo.
/// </summary>
public class TemplateManifestTests
{
    private static TemplateManifest Parse(string json)
    {
        TemplateManifest.TryParse(json, out var manifest, out var errors)
            .Should().BeTrue(string.Join("; ", errors));
        return manifest!;
    }

    private static IReadOnlyList<string> Errors(string json)
    {
        TemplateManifest.TryParse(json, out _, out var errors).Should().BeFalse();
        return errors;
    }

    [Fact]
    public void The_manifests_that_ship_with_harbora_all_parse()
    {
        // The seeded set, verbatim. A schema the built-in templates fail is a schema that is wrong.
        Parse("""{"image":"nginx:alpine","port":80,"volumes":[{"mount":"/usr/share/nginx/html"}],"env":[]}""");
        Parse("""{"source":"git","port":3000,"env":[{"key":"NODE_ENV","default":"production"}]}""");
        Parse("""{"source":"git","port":80,"env":[{"key":"APP_ENV","default":"production"},{"key":"APP_KEY","secret":true}]}""");
        Parse("""{"image":"wordpress:php8.3-apache","port":80,"requires":["mariadb"],"volumes":[{"mount":"/var/www/html"}],"env":[{"key":"WORDPRESS_DB_HOST"},{"key":"WORDPRESS_DB_PASSWORD","secret":true}]}""");
    }

    [Fact]
    public void A_declared_volume_is_read_rather_than_ignored()
    {
        // The gap this closes: the static-site template says its content lives at this path, and the
        // app was created without a volume, so every redeploy started from an empty directory.
        var manifest = Parse("""{"image":"nginx:alpine","port":80,"volumes":[{"mount":"/usr/share/nginx/html"}]}""");

        manifest.Volumes.Should().ContainSingle().Which.MountPath.Should().Be("/usr/share/nginx/html");
    }

    [Fact]
    public void A_secret_variable_is_marked_as_one()
    {
        var manifest = Parse("""{"source":"git","env":[{"key":"APP_KEY","secret":true},{"key":"APP_ENV","default":"production"}]}""");

        manifest.Variables.Should().HaveCount(2);
        manifest.Variables.Single(v => v.Key == "APP_KEY").Secret.Should().BeTrue();
        manifest.Variables.Single(v => v.Key == "APP_ENV").Default.Should().Be("production");
        manifest.Variables.Single(v => v.Key == "APP_ENV").Secret.Should().BeFalse();
    }

    [Fact]
    public void A_manifest_with_nothing_to_deploy_is_refused_with_the_reason()
    {
        Errors("""{"port":80}""").Should().ContainSingle()
            .Which.Should().Contain("image").And.Contain("git");
    }

    [Fact]
    public void A_source_harbora_cannot_build_is_named_in_the_error()
    {
        // "svn" is refused by saying what it is, not by saying "invalid".
        Errors("""{"source":"svn"}""").Should().Contain(e => e.Contains("svn"));
    }

    [Fact]
    public void Broken_json_is_reported_as_broken_json()
    {
        Errors("""{"image": "nginx", }}""").Should().Contain(e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void A_port_outside_the_possible_range_is_caught_here()
    {
        // Otherwise the deploy fails much later, with a message about a health check rather than
        // about the template.
        Errors("""{"image":"nginx","port":70000}""").Should().Contain(e => e.Contains("65535"));
        Errors("""{"image":"nginx","port":0}""").Should().Contain(e => e.Contains("65535"));
    }

    [Fact]
    public void A_port_that_is_not_a_number_is_not_silently_dropped()
    {
        // Ignoring it would deploy on the default port and look like the template was wrong.
        Errors("""{"image":"nginx","port":"80"}""").Should().Contain(e => e.Contains("whole number"));
    }

    [Fact]
    public void A_variable_with_no_name_is_refused()
    {
        Errors("""{"image":"nginx","env":[{"default":"x"}]}""").Should().Contain(e => e.Contains("key"));
    }

    [Fact]
    public void The_same_variable_twice_is_refused_rather_than_resolved_quietly()
    {
        // Which of the two defaults won would be invisible afterwards.
        Errors("""{"image":"nginx","env":[{"key":"A","default":"1"},{"key":"A","default":"2"}]}""")
            .Should().Contain(e => e.Contains("twice"));
    }

    [Fact]
    public void A_relative_mount_path_is_refused()
    {
        // It would mount somewhere nobody intended, and only be noticed as missing data.
        Errors("""{"image":"nginx","volumes":[{"mount":"data"}]}""")
            .Should().Contain(e => e.Contains("absolute"));
    }

    [Fact]
    public void A_list_written_as_something_else_says_what_was_expected()
    {
        Errors("""{"image":"nginx","env":{"APP_ENV":"production"}}""")
            .Should().Contain(e => e.Contains("\"env\" must be a list"));

        Errors("""{"image":"nginx","volumes":"/data"}""")
            .Should().Contain(e => e.Contains("\"volumes\" must be a list"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // Fixing one typo per save, four times, is how people give up on a form.
        var errors = Errors("""{"port":99999,"env":[{"default":"x"}],"volumes":[{"mount":"rel"}]}""");

        errors.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void An_empty_manifest_is_refused_rather_than_treated_as_a_blank_app()
    {
        Errors("").Should().NotBeEmpty();
        Errors("{}").Should().NotBeEmpty();
    }
}
