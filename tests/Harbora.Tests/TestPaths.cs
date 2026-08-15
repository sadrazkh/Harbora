namespace Harbora.Tests;

/// <summary>Locating source files the tests read, from wherever the runner happens to start.</summary>
public static class TestPaths
{
    /// <summary>The Harbora.Web project directory.</summary>
    public static string WebRoot { get; } = Find(Path.Combine("src", "Harbora.Web"));

    /// <summary>The Harbora.Infrastructure project directory.</summary>
    public static string InfrastructureRoot { get; } = Find(Path.Combine("src", "Harbora.Infrastructure"));

    /// <summary>
    /// The tutorial chapters' directory. Lives at the repository root under <c>docs/</c>, not under
    /// <c>src/</c> like the other two — the docs are shipped content, not a project — so this walks
    /// the same way but for a different relative path rather than reusing the <c>src/</c> lookup.
    /// </summary>
    public static string DocsRoot { get; } = Find(Path.Combine("docs", "tutorial"));

    /// <summary>Walks up from the test output directory until <paramref name="relativePath"/> exists.</summary>
    private static string Find(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {relativePath} from the test output directory.");
    }
}
