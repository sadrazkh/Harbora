namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// A versioned Ready App description. The node can hold several versions of the same app id at
/// once, which is the whole point: an upgrade is choosing a different manifest, and a rollback is
/// choosing the previous one — not re-deriving what the previous one was.
/// </summary>
public sealed record AppManifest
{
    public required string AppId { get; init; }

    /// <summary>Version of the template that produced this manifest.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>Version of the application itself, e.g. <c>16.4</c> for PostgreSQL.</summary>
    public required string ApplicationVersion { get; init; }

    public required IReadOnlyList<ManifestImage> Images { get; init; }

    /// <summary>Architectures every image in this manifest is published for.</summary>
    public required IReadOnlyList<string> SupportedArchitectures { get; init; }

    public ResourceLimits RequiredResources { get; init; } = new();

    /// <summary>Declares which env vars exist, which are required, and which are secret.</summary>
    public IReadOnlyList<EnvironmentField> EnvironmentSchema { get; init; } = [];
    public IReadOnlyList<SecretField> SecretSchema { get; init; } = [];

    public IReadOnlyList<VolumeSpec> Volumes { get; init; } = [];
    public IReadOnlyList<NetworkSpec> Networks { get; init; } = [];
    public IReadOnlyList<PortMapping> Ports { get; init; } = [];
    public IReadOnlyList<HttpRouteSpec> HttpRoutes { get; init; } = [];
    public IReadOnlyList<TcpRouteSpec> TcpRoutes { get; init; } = [];

    public HealthCheckSpec? HealthCheck { get; init; }

    public BackupPolicy? Backup { get; init; }
    public RestorePolicy? Restore { get; init; }

    public UpgradeStrategy Upgrade { get; init; } = new();

    /// <summary>Human-readable notes shown before an upgrade that is not purely mechanical.</summary>
    public string? MigrationNotes { get; init; }

    /// <summary>Agent version below which this manifest must be refused.</summary>
    public string? MinimumNodeVersion { get; init; }

    /// <summary>Other app ids that must already be running for this one to work.</summary>
    public IReadOnlyList<ManifestDependency> Dependencies { get; init; } = [];
}

/// <summary>An image referenced by a manifest, pinned by digest per architecture.</summary>
public sealed record ManifestImage
{
    /// <summary>Role of this image within the app, e.g. <c>app</c>, <c>db</c>, <c>worker</c>.</summary>
    public required string Role { get; init; }

    public required string Repository { get; init; }
    public string? Tag { get; init; }

    /// <summary>
    /// Digest per architecture (<c>amd64</c>, <c>arm64</c>). A manifest listing an architecture in
    /// <see cref="AppManifest.SupportedArchitectures"/> with no digest here is invalid — the node
    /// would otherwise fall back to a tag on exactly the platform it was least tested on.
    /// </summary>
    public required IReadOnlyDictionary<string, string> DigestByArchitecture { get; init; }

    public ImageRef? For(string architecture) =>
        DigestByArchitecture.TryGetValue(architecture, out var digest)
            ? new ImageRef { Repository = Repository, Digest = digest, Tag = Tag }
            : null;
}

public sealed record EnvironmentField
{
    public required string Key { get; init; }
    public bool Required { get; init; }
    public string? Default { get; init; }
    public string? Description { get; init; }

    /// <summary>Optional regex the value must satisfy. Validated on the node before deployment.</summary>
    public string? Pattern { get; init; }
}

public sealed record SecretField
{
    public required string Key { get; init; }
    public bool Required { get; init; } = true;

    /// <summary>When set, the control plane generates a value of this length rather than asking a human.</summary>
    public int? GenerateLength { get; init; }

    public SecretMount MountAs { get; init; } = SecretMount.Environment;
    public string? TargetPath { get; init; }
    public string? Description { get; init; }
}

public sealed record BackupPolicy
{
    /// <summary>Volumes whose contents constitute the app's state.</summary>
    public IReadOnlyList<string> Volumes { get; init; } = [];

    /// <summary>Engine hint for a logical dump, e.g. <c>postgres</c>. Null means file-level only.</summary>
    public string? DatabaseEngine { get; init; }

    /// <summary>True when the app must be stopped for a consistent copy.</summary>
    public bool RequiresQuiesce { get; init; }

    public string? Schedule { get; init; }
    public int RetentionDays { get; init; } = 14;
}

public sealed record RestorePolicy
{
    /// <summary>Stop the workload before restoring. Almost always true for a database.</summary>
    public bool StopBeforeRestore { get; init; } = true;

    /// <summary>Commands to run after restore, e.g. a schema migration. Argv arrays, never shell strings.</summary>
    public IReadOnlyList<IReadOnlyList<string>> PostRestoreCommands { get; init; } = [];

    public bool VerifyHealthAfterRestore { get; init; } = true;
}

public sealed record ManifestDependency
{
    public required string AppId { get; init; }

    /// <summary>Minimum acceptable application version of the dependency.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>False when the app degrades gracefully without it.</summary>
    public bool Required { get; init; } = true;
}
