using Harbora.Infrastructure.Storage;

namespace Harbora.Web.ViewModels;

/// <param name="ReadOnly">Mounted read-only, so nothing here can be changed from the panel either.</param>
public sealed record AppDataVolumeViewModel(Guid Id, string Name, string MountPath, bool ReadOnly);

public sealed record AppDataViewModel
{
    public required Guid AppId { get; init; }
    public required string AppName { get; init; }
    public IReadOnlyList<AppDataVolumeViewModel> Volumes { get; init; } = [];
    public Guid? SelectedVolumeId { get; init; }

    /// <summary>Where in the volume, normalised. Empty is the root.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The directory above, or null at the root — which is what hides the "up" link.</summary>
    public string? Parent { get; init; }

    public IReadOnlyList<VolumeEntry> Entries { get; init; } = [];
    public bool IsReadOnly { get; init; }

    /// <summary>Each path segment with the path that reaches it, for the breadcrumb.</summary>
    public IReadOnlyList<(string Name, string Path)> Crumbs
    {
        get
        {
            if (Path.Length == 0) return [];

            var crumbs = new List<(string, string)>();
            var walked = string.Empty;
            foreach (var segment in Path.Split('/'))
            {
                walked = walked.Length == 0 ? segment : $"{walked}/{segment}";
                crumbs.Add((segment, walked));
            }

            return crumbs;
        }
    }
}

public sealed record AppDataEditViewModel
{
    public required Guid AppId { get; init; }
    public required string AppName { get; init; }
    public required Guid VolumeId { get; init; }
    public required string Path { get; init; }
    public required string Content { get; init; }
    public bool IsReadOnly { get; init; }
}

/// <param name="Attached">Whether this application already holds this database's variables.</param>
/// <param name="Prefix">
/// The prefix this database writes under when the plain names are taken by another one. Shown
/// because the second database an application attaches is read from a different variable, and
/// nothing else on the screen would say which.
/// </param>
public sealed record AppDatabaseLinkViewModel(
    Guid Id,
    string Name,
    Harbora.Domain.Common.ManagedServiceType Type,
    string ContainerName,
    bool Attached,
    string Prefix);
