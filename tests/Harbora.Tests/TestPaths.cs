namespace Harbora.Tests;

/// <summary>Locating source files the tests read, from wherever the runner happens to start.</summary>
public static class TestPaths
{
    /// <summary>The Harbora.Web project directory.</summary>
    public static string WebRoot { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Harbora.Web");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Harbora.Web from the test output directory.");
    }
}
