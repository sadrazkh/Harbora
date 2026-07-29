using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Reading a tenant's compose file. The rule these tests enforce is: never run something other than
/// what the author wrote. Silently dropping <c>privileged</c>, a bind mount or
/// <c>network_mode: host</c> would deploy a different application than the file describes, and the
/// difference surfaces later as what looks like a platform bug — so anything not understood is
/// refused by name.
/// </summary>
public class ComposeFileTests
{
    [Fact]
    public void Reads_a_single_service()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx:alpine
                ports:
                  - "8080:80"
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        var web = result.Services.Should().ContainSingle().Subject;
        web.Name.Should().Be("web");
        web.Image.Should().Be("nginx:alpine");
        web.Port.Should().Be(80, "the container side is what we route to, not the host side");
        web.IsWeb.Should().BeTrue();
    }

    [Fact]
    public void Reads_a_multi_service_stack()
    {
        var result = ComposeFile.Parse("""
            version: "3.9"
            services:
              web:
                build: .
                ports:
                  - "3000"
                environment:
                  - NODE_ENV=production
                depends_on:
                  - db
              db:
                image: postgres:16-alpine
                volumes:
                  - pgdata:/var/lib/postgresql/data
            volumes:
              pgdata:
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Services.Select(s => s.Name).Should().Equal("web", "db");

        var web = result.Services[0];
        web.Build.Should().Be(".");
        web.Environment.Should().Contain("NODE_ENV", "production");
        web.DependsOn.Should().Equal("db");
        web.IsWeb.Should().BeTrue();

        result.Services[1].Volumes.Should().Equal(("pgdata", "/var/lib/postgresql/data"));
    }

    // ---- refusals: each of these would change isolation or host access ----

    [Theory]
    [InlineData("privileged: true", "privileged")]
    [InlineData("network_mode: host", "network_mode")]
    [InlineData("pid: host", "pid")]
    [InlineData("cap_add:", "cap_add")]
    [InlineData("devices:", "devices")]
    [InlineData("security_opt:", "security_opt")]
    public void Directives_that_break_isolation_are_refused_by_name(string directive, string keyword)
    {
        var result = ComposeFile.Parse($"""
            services:
              web:
                image: nginx
                ports:
                  - "80"
                {directive}
            """);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains(keyword),
            "the message has to name the directive, not just say 'unsupported'");
    }

    [Fact]
    public void A_bind_mount_is_refused_because_it_exposes_the_host()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - "80"
                volumes:
                  - ./site:/usr/share/nginx/html
            """);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("bind mount") && e.Contains("named volume"),
            "refusing is only useful if it says what to do instead");
    }

    [Fact]
    public void An_absolute_host_path_is_refused_too()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - "80"
                volumes:
                  - /etc/passwd:/tmp/passwd
            """);

        result.IsValid.Should().BeFalse();
    }

    // ---- routing has to be unambiguous ----

    [Fact]
    public void Two_published_ports_without_a_web_service_is_an_error()
    {
        // Picking one silently would be a coin flip that only shows up as "the wrong site is live".
        var result = ComposeFile.Parse("""
            services:
              api:
                image: api:1
                ports:
                  - "3000"
              admin:
                image: admin:1
                ports:
                  - "4000"
            """);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("api") && e.Contains("admin"));
    }

    [Fact]
    public void A_service_named_web_resolves_the_ambiguity()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: web:1
                ports:
                  - "3000"
              admin:
                image: admin:1
                ports:
                  - "4000"
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Web!.Name.Should().Be("web");
    }

    [Fact]
    public void A_stack_with_no_published_port_is_an_error()
    {
        var result = ComposeFile.Parse("""
            services:
              worker:
                image: worker:1
            """);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("nothing to route"));
    }

    // ---- structural validation ----

    [Fact]
    public void A_service_needs_an_image_or_a_build()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                ports:
                  - "80"
            """);

        result.Errors.Should().Contain(e => e.Contains("neither"));
    }

    [Fact]
    public void Setting_both_image_and_build_is_ambiguous()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx
                build: .
                ports:
                  - "80"
            """);

        result.Errors.Should().Contain(e => e.Contains("both"));
    }

    [Fact]
    public void An_empty_file_says_what_is_missing()
    {
        ComposeFile.Parse("").Errors.Should().Contain(e => e.Contains("services"));
    }

    // ---- accepted, but neutralised ----

    [Fact]
    public void Container_name_is_ignored_with_a_warning()
    {
        // Harbora names containers so old and new can coexist during a cutover.
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx
                container_name: my-fixed-name
                ports:
                  - "80"
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Warnings.Should().Contain(w => w.Contains("container_name"));
    }

    [Fact]
    public void Unknown_keys_warn_rather_than_fail()
    {
        var result = ComposeFile.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - "80"
                some_future_key: value
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Warnings.Should().Contain(w => w.Contains("some_future_key"));
    }

    [Fact]
    public void Comments_do_not_confuse_the_parser()
    {
        var result = ComposeFile.Parse("""
            # a stack
            services:
              web:            # the only service
                image: nginx  # pinned later
                ports:
                  - "80"
            """);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Services[0].Image.Should().Be("nginx");
    }

    [Fact]
    public void Command_is_read_in_both_forms()
    {
        var json = ComposeFile.Parse("""
            services:
              web:
                image: node
                ports:
                  - "3000"
                command: ["npm", "start"]
            """);
        json.Services[0].Command.Should().Equal("npm", "start");

        var shell = ComposeFile.Parse("""
            services:
              web:
                image: node
                ports:
                  - "3000"
                command: npm start
            """);
        shell.Services[0].Command.Should().Equal("npm", "start");
    }

    [Fact]
    public void Environment_is_read_in_both_forms()
    {
        var list = ComposeFile.Parse("""
            services:
              web:
                image: node
                ports:
                  - "3000"
                environment:
                  - A=1
            """);
        list.Services[0].Environment.Should().Contain("A", "1");

        var map = ComposeFile.Parse("""
            services:
              web:
                image: node
                ports:
                  - "3000"
                environment:
                  B: 2
            """);
        map.Services[0].Environment.Should().Contain("B", "2");
    }
}
