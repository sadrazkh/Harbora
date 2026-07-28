using FluentAssertions;
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
    public void Detects_Python_from_requirements()
    {
        Touch("requirements.txt", "flask");
        Buildpacks.Detect(_dir, 5000)!.Value.Stack.Should().Be("Python");
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
