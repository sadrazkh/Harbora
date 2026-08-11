using System.Security.Cryptography;
using System.Text;

namespace Harbora.Infrastructure.Backups;

public enum ArtifactRelayDirection { UploadToPanel, DownloadFromPanel }

public sealed record ArtifactRelayTicket(Guid Id, string Token);
public sealed record ArtifactRelayLease(
    Guid Id, ArtifactRelayDirection Direction, string Path, SemaphoreSlim Gate);

/// <summary>
/// In-memory, one-use handoff between an enrolled node and the panel. Only a hash of the bearer
/// token is retained, and a valid request consumes its slot before any bytes move.
/// </summary>
public sealed class ArtifactRelayRegistry(TimeProvider clock)
{
    private sealed record Slot(
        Guid Id, ArtifactRelayDirection Direction, string Path, byte[] TokenHash,
        DateTimeOffset ExpiresAt, SemaphoreSlim Gate);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = [];

    public ArtifactRelayTicket CreateUpload(string destinationPath) =>
        Create(ArtifactRelayDirection.UploadToPanel, destinationPath);

    public ArtifactRelayTicket CreateDownload(string sourcePath) =>
        Create(ArtifactRelayDirection.DownloadFromPanel, sourcePath);

    public bool TryConsume(
        Guid id, string? token, ArtifactRelayDirection direction, out ArtifactRelayLease? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (_gate)
        {
            SweepExpired();
            if (!_slots.TryGetValue(id, out var slot) || slot.Direction != direction) return false;

            var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            if (!CryptographicOperations.FixedTimeEquals(candidate, slot.TokenHash)) return false;

            _slots.Remove(id);
            lease = new ArtifactRelayLease(slot.Id, slot.Direction, slot.Path, slot.Gate);
            return true;
        }
    }

    public bool TryAuthorize(
        Guid id, string? token, ArtifactRelayDirection direction, out ArtifactRelayLease? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (_gate)
        {
            SweepExpired();
            if (!_slots.TryGetValue(id, out var slot) || slot.Direction != direction) return false;
            var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            if (!CryptographicOperations.FixedTimeEquals(candidate, slot.TokenHash)) return false;
            lease = new ArtifactRelayLease(slot.Id, slot.Direction, slot.Path, slot.Gate);
            return true;
        }
    }

    public void Revoke(Guid id)
    {
        Slot? removed = null;
        lock (_gate)
        {
            if (_slots.Remove(id, out var slot)) removed = slot;
        }
        if (removed is not null) DeletePartial(removed);
    }

    private ArtifactRelayTicket Create(ArtifactRelayDirection direction, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var slot = new Slot(
            Guid.NewGuid(), direction, fullPath,
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            clock.GetUtcNow().AddHours(1), new SemaphoreSlim(1, 1));

        lock (_gate)
        {
            SweepExpired();
            _slots.Add(slot.Id, slot);
        }

        return new ArtifactRelayTicket(slot.Id, token);
    }

    private void SweepExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var id in _slots.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
            if (_slots.Remove(id, out var slot)) DeletePartial(slot);
    }

    public static string PartialPath(ArtifactRelayLease lease) =>
        lease.Path + ".relay-" + lease.Id.ToString("n") + ".tmp";

    private static void DeletePartial(Slot slot)
    {
        if (slot.Direction != ArtifactRelayDirection.UploadToPanel) return;
        var partial = slot.Path + ".relay-" + slot.Id.ToString("n") + ".tmp";
        if (File.Exists(partial)) try { File.Delete(partial); } catch { /* best effort */ }
    }
}
