using FluentAssertions;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What an app created from a template is actually given.
///
/// Before this, the manifest was read for its image and port and nothing else: a template declaring
/// a volume produced an app without one, and a static site's content vanished on every redeploy.
/// </summary>
public class TemplateSetupTests
{
    private static TemplateManifest Parse(string json)
    {
        TemplateManifest.TryParse(json, out var manifest, out var errors)
            .Should().BeTrue(string.Join("; ", errors));
        return manifest!;
    }

    private static TemplateSetupPlan Plan(string json, string secret = "generated-secret")
        => TemplateSetup.Prepare(Parse(json), () => secret);

    [Fact]
    public void The_volume_a_template_declares_is_created_with_the_app()
    {
        var plan = Plan("""{"image":"nginx:alpine","port":80,"volumes":[{"mount":"/usr/share/nginx/html"}]}""");

        plan.VolumeMounts.Should().BeEquivalentTo(["/usr/share/nginx/html"]);
    }

    [Fact]
    public void A_secret_with_no_default_is_generated_rather_than_left_empty()
    {
        // A framework that will not boot without an application key should not require the person
        // deploying it to know that.
        var plan = Plan("""{"source":"git","env":[{"key":"APP_KEY","secret":true}]}""", secret: "abc123");

        var key = plan.Variables.Should().ContainSingle().Subject;
        key.Value.Should().Be("abc123");
        key.Secret.Should().BeTrue();
        key.NeedsAValue.Should().BeFalse();
    }

    [Fact]
    public void A_default_is_used_as_given_rather_than_regenerated()
    {
        var plan = Plan("""{"source":"git","env":[{"key":"NODE_ENV","default":"production"}]}""");

        plan.Variables.Should().ContainSingle().Which.Value.Should().Be("production");
    }

    [Fact]
    public void A_secret_that_has_a_default_keeps_it()
    {
        // Overwriting a value the template author chose deliberately would be worse than useless.
        var plan = Plan("""{"source":"git","env":[{"key":"TOKEN","default":"fixed","secret":true}]}""");

        plan.Variables.Should().ContainSingle().Which.Value.Should().Be("fixed");
    }

    [Fact]
    public void A_plain_variable_with_no_default_is_left_for_a_person_and_flagged()
    {
        // A hostname is something only whoever is deploying knows. Inventing one would produce an
        // app that looks configured and cannot work.
        var plan = Plan("""{"image":"wordpress","env":[{"key":"WORDPRESS_DB_HOST"}]}""");

        var variable = plan.Variables.Should().ContainSingle().Subject;
        variable.Value.Should().BeNull();
        variable.NeedsAValue.Should().BeTrue();
    }

    [Fact]
    public void What_still_needs_doing_is_said_out_loud()
    {
        // Silence about a variable someone still has to fill in is how an app sits broken with no
        // indication why.
        var plan = Plan("""{"image":"wordpress","requires":["mariadb"],"env":[{"key":"WORDPRESS_DB_HOST"}]}""");

        var advice = TemplateSetup.Advice(plan);

        advice.Should().Contain("WORDPRESS_DB_HOST");
        advice.Should().Contain("mariadb");
    }

    [Fact]
    public void A_template_that_needs_nothing_further_says_nothing()
    {
        // The guard on the message above: advice shown every time is advice nobody reads.
        var plan = Plan("""{"image":"nginx:alpine","port":80,"env":[{"key":"A","default":"1"}]}""");

        TemplateSetup.Advice(plan).Should().BeNull();
    }

    [Fact]
    public void The_same_mount_twice_produces_one_volume()
    {
        var manifest = new TemplateManifest
        {
            Image = "nginx",
            Volumes = [new ManifestVolume("/data"), new ManifestVolume("/data")]
        };

        TemplateSetup.Prepare(manifest, () => "x").VolumeMounts.Should().ContainSingle();
    }

    [Fact]
    public void Each_generated_secret_is_asked_for_separately()
    {
        // Two variables sharing one generated value would tie them together for ever, and nothing
        // would show it.
        var calls = 0;
        var manifest = Parse("""{"source":"git","env":[{"key":"A","secret":true},{"key":"B","secret":true}]}""");

        TemplateSetup.Prepare(manifest, () => $"secret-{++calls}");

        calls.Should().Be(2);
    }
}
