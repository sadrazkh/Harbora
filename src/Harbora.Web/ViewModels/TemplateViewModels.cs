using System.ComponentModel.DataAnnotations;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;

namespace Harbora.Web.ViewModels;

public sealed class TemplateCatalogItemViewModel
{
    public required AppTemplate Template { get; init; }
    public required TemplateManifest Manifest { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int ResourceCount => 1 + Manifest.Requires.Count;
    public bool IsStack => Manifest.Requires.Count > 0;
    public bool IsManagedService => !string.IsNullOrWhiteSpace(Manifest.Service);
    public bool NeedsRepository => Manifest.Source?.Equals("git", StringComparison.OrdinalIgnoreCase) == true;

    public static TemplateCatalogItemViewModel? Create(AppTemplate template, bool isFa)
    {
        if (!TemplateManifest.TryParse(template.ManifestJson, out var manifest, out _)) return null;

        return new TemplateCatalogItemViewModel
        {
            Template = template,
            Manifest = manifest!,
            Name = isFa && !string.IsNullOrWhiteSpace(template.NameFa) ? template.NameFa : template.Name,
            Description = isFa && !string.IsNullOrWhiteSpace(template.DescriptionFa)
                ? template.DescriptionFa
                : template.Description
        };
    }
}

public sealed class TemplateCatalogPageViewModel
{
    public IReadOnlyList<TemplateCatalogItemViewModel> Items { get; init; } = [];
    public IReadOnlyList<AppTemplate> Reviewing { get; init; } = [];
    public Guid WorkspaceId { get; init; }
    public string? Query { get; init; }
    public string? Category { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
}

public sealed class TemplateDeployInput
{
    public Guid TemplateId { get; set; }
    [Required, MaxLength(80)] public string ProjectName { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string ResourceName { get; set; } = string.Empty;
    public string? RepositoryUrl { get; set; }
    public string? GitRef { get; set; } = "main";
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DeployNow { get; set; } = true;
}

public sealed class TemplateDeployPageViewModel
{
    public required TemplateCatalogItemViewModel Item { get; init; }
    public required TemplateDeployInput Input { get; init; }
}
