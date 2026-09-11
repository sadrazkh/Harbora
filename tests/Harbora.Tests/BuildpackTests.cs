using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Characterization tests for zero-config buildpack detection. Pins detection order and per-stack
/// output before the overhaul refreshes base images / pins digests (R-BLD-1, doc 12 P7).
/// </summary>
public class BuildpackTests : IDisposable
{
    private readonly string _dir;

    public BuildpackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hbr-bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string name, string content = "")
        => File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Detects_Node_from_package_json()
    {
        Touch("package.json", "{}");
        var pack = Buildpacks.Detect(_dir, 3000);
        pack.Should().NotBeNull();
        pack!.Value.Stack.Should().Be("Node.js");
        pack.Value.Dockerfile.Should().Contain("EXPOSE 3000");
    }

    [Fact]
    public void Detects_Go_from_go_mod()
    {
        Touch("go.mod", "module x");
        Buildpacks.Detect(_dir, 8080)!.Value.Stack.Should().Be("Go");
    }

    [Fact]
    public void Detects_Python_from_requirements_with_a_recognised_entry_file()
    {
        Touch("requirements.txt", "flask");
        Touch("main.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Stack.Should().Be("Python");
    }

    [Fact]
    public void Detects_bot_py_and_it_appears_in_the_generated_CMD()
    {
        Touch("requirements.txt", "python-telegram-bot");
        Touch("bot.py");
        var pack = Buildpacks.Detect(_dir, 5000);
        pack.Should().NotBeNull();
        pack!.Value.Entry.Should().Be("bot.py");
        pack.Value.Dockerfile.Should().Contain("""CMD ["python", "bot.py"]""");
    }

    [Fact]
    public void Requirements_txt_with_no_recognisable_entry_file_returns_null_rather_than_guessing()
    {
        Touch("requirements.txt", "flask");
        Buildpacks.Detect(_dir, 5000).Should().BeNull(
            "guessing an entry file that doesn't exist produces an image that can never start");
    }

    [Fact]
    public void Main_py_still_wins_over_bot_py_when_both_exist()
    {
        Touch("requirements.txt", "flask");
        Touch("main.py");
        Touch("bot.py");
        var pack = Buildpacks.Detect(_dir, 5000);
        pack!.Value.Entry.Should().Be("main.py", "precedence is stable so an already-deployed repo keeps choosing the same file");
    }

    [Fact]
    public void Manage_py_alone_no_longer_detects_as_a_runnable_Python_app()
    {
        Touch("requirements.txt", "django");
        Touch("manage.py");
        Buildpacks.Detect(_dir, 8000).Should().BeNull(
            "`python manage.py` with no arguments prints usage and exits 0 — a crash loop, not a running app");
    }

    [Fact]
    public void Generated_Python_Dockerfile_sets_PYTHONUNBUFFERED_so_logs_are_not_silently_buffered()
    {
        Touch("requirements.txt", "flask");
        Touch("app.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Dockerfile.Should().Contain("ENV PYTHONUNBUFFERED=1");
    }

    [Fact]
    public void Worker_kind_Python_detection_claims_neither_a_port_nor_EXPOSE()
    {
        Touch("requirements.txt", "python-telegram-bot");
        Touch("bot.py");
        var df = Buildpacks.Detect(_dir, 5000, ServiceKind.Worker)!.Value.Dockerfile;
        df.Should().NotContain("EXPOSE");
        df.Should().NotContain("ENV PORT");
    }

    [Fact]
    public void Web_kind_Python_detection_claims_both_a_port_and_EXPOSE()
    {
        Touch("requirements.txt", "flask");
        Touch("app.py");
        var df = Buildpacks.Detect(_dir, 5000, ServiceKind.Web)!.Value.Dockerfile;
        df.Should().Contain("EXPOSE 5000");
        df.Should().Contain("ENV PORT=5000");
    }

    [Fact]
    public void Python_result_carries_the_entry_name_so_the_pipeline_can_print_it()
    {
        Touch("requirements.txt", "flask");
        Touch("server.py");
        Buildpacks.Detect(_dir, 5000)!.Value.Entry.Should().Be("server.py");
    }

    [Fact]
    public void DetectionHint_names_what_was_looked_for_when_a_Python_project_has_no_entry_file()
    {
        Touch("requirements.txt", "flask");
        Buildpacks.DetectionHint(_dir).Should().Contain("app.py").And.Contain("bot.py");
    }

    [Fact]
    public void DetectionHint_calls_out_manage_py_as_Django_without_offering_to_support_it()
    {
        Touch("requirements.txt", "django");
        Touch("manage.py");
        Buildpacks.DetectionHint(_dir).Should().ContainEquivalentOf("Django");
    }

    [Fact]
    public void DetectionHint_is_null_when_nothing_looks_like_Python()
    {
        Touch("README.md", "# nothing to build");
        Buildpacks.DetectionHint(_dir).Should().BeNull();
    }

    [Fact]
    public void Detects_PHP_from_index_php()
    {
        Touch("index.php", "<?php");
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Be("PHP");
    }

    [Fact]
    public void Detects_static_site_from_index_html()
    {
        Touch("index.html", "<html></html>");
        Buildpacks.Detect(_dir, 80)!.Value.Stack.Should().Contain("Static");
    }

    [Fact]
    public void Node_takes_precedence_over_static_when_both_present()
    {
        Touch("package.json", "{}");
        Touch("index.html", "<html></html>");
        Buildpacks.Detect(_dir, 3000)!.Value.Stack.Should().Be("Node.js",
            "detection order is most-specific first");
    }

    [Fact]
    public void Returns_null_when_nothing_recognizable()
    {
        Touch("README.md", "# nothing to build");
        Buildpacks.Detect(_dir, 80).Should().BeNull();
    }
}
