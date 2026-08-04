namespace Harbora.NodeAgent.Tests;

/// <summary>Locating repository files the tests read, from wherever the runner happens to start.</summary>
public static class RepoPaths
{
    /// <summary>Repository root — the directory that holds <c>Harbora.slnx</c>.</summary>
    public static string Root { get; } = Find();

    /// <summary><c>contracts/node-agent/v1</c>.</summary>
    public static string ContractV1 { get; } = Path.Combine(Root, "contracts", "node-agent", "v1");

    /// <summary><c>deploy/node-agent</c>.</summary>
    public static string DeployNodeAgent { get; } = Path.Combine(Root, "deploy", "node-agent");

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harbora.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Harbora.slnx from the test output directory.");
    }
}
