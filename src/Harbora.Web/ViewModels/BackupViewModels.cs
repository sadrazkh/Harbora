using Harbora.Domain.Backups;

namespace Harbora.Web.ViewModels;

/// <summary>Everything the Backups screen renders in one page: history, targets, destinations, schedules.</summary>
public sealed class BackupsPageViewModel
{
    public List<Backup> Backups { get; set; } = new();
    public List<BackupDestination> Destinations { get; set; } = new();
    public List<BackupSchedule> Schedules { get; set; } = new();

    /// <summary>Selectable backup targets encoded as "Type|ref" with a friendly label.</summary>
    public List<(string Value, string Label)> Targets { get; set; } = new();
}
