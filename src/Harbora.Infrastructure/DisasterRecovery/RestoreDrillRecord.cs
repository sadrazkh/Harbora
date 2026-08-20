using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.DisasterRecovery;

/// <summary>
/// Sub-project 12 — the "last drill" surface the admin settings page shows. Every write comes from
/// <c>harbora record-drill-result</c>, which <c>deploy/restore-drill.sh</c> calls exactly once,
/// unconditionally, as the very last thing it does — whether the drill passed or failed. That call
/// order matters more than anything in this file: a drill that dies restoring the dump and never
/// reaches the recording step must not leave the page quietly repeating whatever the last *good*
/// run said. Read <see cref="Read"/>'s own remarks for how that silence is told apart from an
/// honest failure.
///
/// <para>
/// Three <see cref="Setting"/> rows, no new table — the same "planner default" this codebase
/// reaches for when a feature needs to remember one small fact rather than a history of them
/// (<c>SettingKeys.CloudflareLastVerifiedAt</c> is the precedent this follows).
/// </para>
/// </summary>
public static class RestoreDrillRecord
{
    /// <summary>The only two words <see cref="WriteAsync"/> accepts, case-insensitively.</summary>
    public const string Pass = "pass";
    public const string Fail = "fail";

    /// <summary>
    /// A verdict older than this reads as stale on the admin page. Thirty days follows the plan's
    /// own "any time — but before, not after, the next infrastructure incident" cadence: a monthly
    /// drill is the loosest schedule that still means something.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Free-text detail is capped rather than rejected past this length — the drill script's own
    /// failure messages are meant to be read on the admin page, not truncated by a form nobody
    /// asked to bound, but an unbounded column is also how one bad run turns into an unbounded row.
    /// </summary>
    public const int MaxDetailLength = 4000;

    /// <summary>
    /// Persists one drill's outcome. Rejects anything that is not exactly "pass" or "fail" rather
    /// than storing it anyway — a typo in the verdict argument must be loud on the command line, not
    /// silently readable later as "whatever the script happened to pass".
    /// </summary>
    public static async Task<RestoreDrillWriteResult> WriteAsync(
        HarboraDbContext db, string? verdict, string? detail, DateTimeOffset at, CancellationToken ct = default)
    {
        var normalized = verdict?.Trim().ToLowerInvariant();
        if (normalized is not (Pass or Fail))
            return RestoreDrillWriteResult.Rejected(
                $"--verdict must be '{Pass}' or '{Fail}', not {(string.IsNullOrEmpty(verdict) ? "(empty)" : $"'{verdict}'")}.");

        var trimmedDetail = (detail ?? string.Empty).Trim();
        if (trimmedDetail.Length > MaxDetailLength)
            trimmedDetail = trimmedDetail[..MaxDetailLength];

        await UpsertAsync(db, SettingKeys.DrLastDrillAt, at.ToString("O"), ct);
        await UpsertAsync(db, SettingKeys.DrLastDrillVerdict, normalized, ct);
        await UpsertAsync(db, SettingKeys.DrLastDrillDetail, trimmedDetail, ct);
        await db.SaveChangesAsync(ct);

        return RestoreDrillWriteResult.Recorded(normalized, at);
    }

    /// <summary>
    /// What the admin page reads. <see cref="RestoreDrillStatus.HasRun"/> is false — not "verdict
    /// unknown" or a fabricated failure — when no drill has ever recorded a result at all: the same
    /// "not measured yet" honesty every other table in this panel owes a reader, per do-not-change
    /// item 18. <see cref="RestoreDrillStatus.IsStale"/> is computed here, against the caller's own
    /// clock, rather than cached at write time, so a page opened long after the last drill sees a
    /// warning that keeps advancing with the calendar instead of one fixed the day it was written.
    /// </summary>
    public static async Task<RestoreDrillStatus> ReadAsync(
        HarboraDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.DrLastDrillAt
                        || s.Key == SettingKeys.DrLastDrillVerdict
                        || s.Key == SettingKeys.DrLastDrillDetail)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var at = rows.TryGetValue(SettingKeys.DrLastDrillAt, out var atRaw)
                 && DateTimeOffset.TryParse(atRaw, out var parsedAt)
            ? parsedAt
            : (DateTimeOffset?)null;
        var verdict = rows.GetValueOrDefault(SettingKeys.DrLastDrillVerdict);
        var detail = rows.GetValueOrDefault(SettingKeys.DrLastDrillDetail);

        if (at is null || verdict is not (Pass or Fail))
            return RestoreDrillStatus.NeverRun;

        return new RestoreDrillStatus(
            HasRun: true, At: at, Verdict: verdict, Detail: string.IsNullOrEmpty(detail) ? null : detail,
            IsStale: now - at.Value > StaleAfter);
    }

    private static async Task UpsertAsync(HarboraDbContext db, string key, string value, CancellationToken ct)
    {
        var setting = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            setting = new Setting { Key = key };
            db.Settings.Add(setting);
        }

        setting.Value = value;
    }
}

/// <summary>The outcome of trying to write one drill result — a rejection is not an exception.</summary>
public sealed record RestoreDrillWriteResult(bool Success, string? Verdict, DateTimeOffset? At, string? Error)
{
    public static RestoreDrillWriteResult Recorded(string verdict, DateTimeOffset at) => new(true, verdict, at, null);
    public static RestoreDrillWriteResult Rejected(string error) => new(false, null, null, error);
}

/// <summary>
/// What the admin page has to say about the last drill. <see cref="HasRun"/> false is the "not
/// measured yet" state — every field beside it is meaningless and must not be read.
/// </summary>
public sealed record RestoreDrillStatus(bool HasRun, DateTimeOffset? At, string? Verdict, string? Detail, bool IsStale)
{
    public static readonly RestoreDrillStatus NeverRun = new(false, null, null, null, false);
}
