using Harbora.Domain.Apps;

namespace Harbora.Infrastructure.Storage;

/// <summary>
/// HARBORA-0033's brake. Every path that would destroy a volume's data — not merely unmount it — is
/// expected to call <see cref="GuardAgainstDestroying"/> before it touches anything: the container,
/// the row, the Docker volume, all of it. A refusal here has to leave nothing half-done, so this
/// throws rather than returning a result a caller could forget to check — the same shape
/// <c>QuotaRefusedException</c> already uses for a different kind of "no" a controller has to show.
///
/// <para>
/// Deliberately narrow: it only refuses the calls that would actually destroy bytes
/// (<c>IDockerEngine.RemoveVolumeAsync</c>). Detaching a volume without deleting its data — unmounting
/// it from an app, or deleting an app with <c>removeVolumes: false</c> — leaves the data on the server
/// exactly as it would for an unprotected volume, so <c>Protected</c> has nothing to say about it. See
/// <see cref="Volume.Protected"/> for why that line is drawn there.
/// </para>
/// </summary>
public static class VolumeProtection
{
    /// <summary>
    /// Refuses if any of these volumes is <see cref="Volume.Protected"/>. Call this before the first
    /// side effect of a destructive delete — before the container is stopped, before the row is
    /// removed, before <c>SaveChangesAsync</c> — so a refusal is always a clean no-op rather than a
    /// partial one a caller has to reason about.
    /// </summary>
    public static void GuardAgainstDestroying(IEnumerable<Volume> volumes)
    {
        var blocked = volumes.Where(v => v.Protected).ToList();
        if (blocked.Count > 0) throw new VolumeProtectedException(blocked);
    }
}

/// <summary>
/// Thrown by <see cref="VolumeProtection.GuardAgainstDestroying"/>. Carries both languages the panel
/// shows, the same way <c>QuotaRefusedException</c> carries <c>ReasonFa</c> alongside its own English
/// <see cref="Exception.Message"/> — a controller picks whichever the current request's culture wants.
/// </summary>
public sealed class VolumeProtectedException(IReadOnlyList<Volume> volumes) : InvalidOperationException(
    BuildMessage(volumes))
{
    public IReadOnlyList<Volume> Volumes { get; } = volumes;

    public string ReasonFa { get; } = BuildMessageFa(volumes);

    private static string BuildMessage(IReadOnlyList<Volume> volumes)
    {
        var noun = volumes.Count == 1 ? "Volume" : "Volumes";
        var verb = volumes.Count == 1 ? "is" : "are";
        var names = string.Join(", ", volumes.Select(v => $"\"{v.MountPath}\""));
        return $"{noun} {names} {verb} Protected. Turn off Protected before deleting its data.";
    }

    private static string BuildMessageFa(IReadOnlyList<Volume> volumes)
    {
        var noun = volumes.Count == 1 ? "والیوم" : "والیوم‌های";
        var names = string.Join("، ", volumes.Select(v => $"«{v.MountPath}»"));
        return $"{noun} {names} محافظت‌شده است. برای حذف داده‌هایش، ابتدا محافظت را خاموش کنید.";
    }
}
