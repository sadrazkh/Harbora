using Harbora.Domain.Backups;
using Harbora.Domain.Common;

namespace Harbora.Web.ViewModels;

/// <summary>Everything the Backups screen renders in one page: history, targets, destinations, schedules.</summary>
public sealed class BackupsPageViewModel
{
    public List<Backup> Backups { get; set; } = new();
    public List<BackupDestination> Destinations { get; set; } = new();
    public List<BackupSchedule> Schedules { get; set; } = new();

    /// <summary>Channels that receive a copy of each finished backup (Telegram, email).</summary>
    public List<BackupDelivery> Deliveries { get; set; } = new();

    /// <summary>Selectable backup targets encoded as "Type|ref" with a friendly label.</summary>
    public List<(string Value, string Label)> Targets { get; set; } = new();
}

/// <summary>
/// What every Backups partial needs, gathered once by <c>Index.cshtml</c> instead of each of the five
/// sections (quick actions, destinations, schedules, deliveries, history) re-deriving it or reaching
/// back into a parent view's locals a Razor partial cannot actually see.
///
/// <para>
/// The four lookup functions used to be local functions inside <c>Index.cshtml</c>'s own
/// <c>@{ }</c> block. A partial gets its own page context — it does not inherit the caller's local
/// functions — so splitting the 690-line file into one section per concern meant these had to travel
/// as data. Closures rather than a static helper class: each one still reads <c>Model.Targets</c> /
/// <c>Model.Destinations</c> / <c>isFa</c> exactly as before, just captured once here.
/// </para>
/// </summary>
public sealed class BackupsViewContext
{
    public required BackupsPageViewModel Page { get; init; }
    public required bool IsFa { get; init; }

    /// <summary>A schedule or a backup is OF, in the words the target list already uses.</summary>
    public required Func<BackupType, string, string> TargetLabel { get; init; }
    public required Func<Guid, string> DestinationName { get; init; }

    /// <summary>Unknown size is not zero bytes — see the doc comment on the call site.</summary>
    public required Func<long, string> Size { get; init; }

    /// <summary>A backup's run status, as the <c>Tone</c> string the shared status-pill partial expects.</summary>
    public required Func<BackupStatus, string> StatusTone { get; init; }

    /// <summary>
    /// Whether the "SFTP connection details" disclosure inside the add-destination card starts open:
    /// true in Advanced mode, or in Simple mode when the destination form was just rejected — the
    /// PanelMode fold-never-remove principle (do-not-change list item 23) applied to this page's one
    /// piece of genuinely specialist material.
    /// </summary>
    public required bool SftpAdvancedOpen { get; init; }

    /// <summary>
    /// What a rejected "add destination" SFTP submission carried, so the redirect back does not throw
    /// away everything but the one missing field. Never the password: nothing was stored yet for a
    /// destination that was just refused, so there is nothing a blank box could quietly discard.
    /// </summary>
    public RejectedSftpSubmission? RejectedSftp { get; init; }
}

/// <summary>The non-secret fields of an "add destination" SFTP submission the server just refused.</summary>
public sealed record RejectedSftpSubmission(
    string? Name, string? Host, int Port, string? Username, string? Directory, string? HostKey);
