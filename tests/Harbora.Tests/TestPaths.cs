namespace Harbora.Tests;

/// <summary>Locating source files the tests read, from wherever the runner happens to start.</summary>
public static class TestPaths
{
    /// <summary>The Harbora.Web project directory.</summary>
    public static string WebRoot { get; } = Find("Harbora.Web");

    /// <summary>The Harbora.Infrastructure project directory.</summary>
    public static string InfrastructureRoot { get; } = Find("Harbora.Infrastructure");

    private static string Find(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", project);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate src/{project} from the test output directory.");
    }
}
