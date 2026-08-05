using System.Net.Http.Json;
using System.Text.Json;
using Harbora.Data;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Sync.Infrastructure;

/// <summary>
/// Drives Syncthing through its REST API.
///
/// <para>
/// The API server is used here — unlike Kopia, where the CLI was chosen — because Syncthing IS a
/// long-running daemon either way. There is no short-lived invocation to prefer: the process holds
/// the folders open and the API is how it is configured. No shell and no process is started by this
/// class at all.
/// </para>
/// <para>
/// The endpoint must be loopback or a private network. Syncthing's API is a direct path to every
/// file it holds, with its own authentication, and publishing it would put a second front door on
/// the platform (THREAT_MODEL T7).
/// </para>
/// <para>
/// <b>Version.</b> Written against Syncthing's <c>/rest/config</c> API (v1.23+, where per-folder and
/// per-device config endpoints replaced whole-config PUTs). Like the Kopia flags, this could not be
/// exercised here — no Syncthing daemon on the machine this branch was written on. The merge guide
/// lists a smoke test as a pre-enable step.
/// </para>
/// </summary>
public sealed class SyncthingSyncEngine(
    HttpClient http,
    HarboraDbContext db,
    IOptions<SyncthingOptions> options,
    ILogger<SyncthingSyncEngine> logger) : ISyncEngine
{
    private readonly SyncthingOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SyncDeviceResult> RegisterDeviceAsync(
        RegisterSyncDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SyncValidation.IsValidDeviceId(request.EngineDeviceId))
            return new SyncDeviceResult(false, request.DeviceId,
                Error: "That does not look like a device id. Copy it from the other device exactly.");

        var deviceId = SyncValidation.NormaliseDeviceId(request.EngineDeviceId);

        var body = new
        {
            deviceID = deviceId,
            name = request.Name,
            addresses = request.Addresses is { Count: > 0 } ? request.Addresses : ["dynamic"],
            // An untrusted device receives ciphertext only. Recorded on the device itself so the
            // engine enforces it for every folder, not just the one being shared today.
            untrusted = request.Untrusted
        };

        var response = await SendAsync(HttpMethod.Post, "/rest/config/devices", body, cancellationToken);

        return response.Succeeded
            ? new SyncDeviceResult(true, request.DeviceId, deviceId)
            : new SyncDeviceResult(false, request.DeviceId, Error: response.Error);
    }

    public async Task<SyncFolderResult> CreateFolderAsync(
        CreateSyncFolderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A stable engine id derived from ours, so a retry addresses the same folder rather than
        // creating a second one pointed at the same directory.
        var engineFolderId = $"harbora-{request.FolderId:N}";

        var body = new
        {
            id = engineFolderId,
            label = request.Label,
            path = request.Path,
            type = FolderType(request.Mode),
            versioning = Versioning(request.Versioning, request.VersioningParameter),
            // Shared with nobody yet: devices are added by PairDeviceAsync, so a folder never exists
            // in a state where it is sharing with a device Harbora has not recorded.
            devices = Array.Empty<object>()
        };

        var response = await SendAsync(HttpMethod.Post, "/rest/config/folders", body, cancellationToken);
        if (!response.Succeeded) return new SyncFolderResult(false, request.FolderId, Error: response.Error);

        if (request.IgnorePatterns is { Count: > 0 })
        {
            var ignores = await SendAsync(HttpMethod.Post,
                $"/rest/db/ignores?folder={Uri.EscapeDataString(engineFolderId)}",
                new { ignore = request.IgnorePatterns }, cancellationToken);

            // The folder exists and syncs; ignore patterns not applying is a real problem but not a
            // reason to report that nothing was created.
            if (!ignores.Succeeded)
                logger.LogWarning("Folder {Folder} was created but its ignore patterns were rejected: {Error}",
                    engineFolderId, ignores.Error);
        }

        return new SyncFolderResult(true, request.FolderId, engineFolderId);
    }

    public async Task<PairDeviceResult> PairDeviceAsync(
        PairSyncDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (space, device, error) = await LoadPairAsync(request, cancellationToken);
        if (space?.EngineFolderId is null || device is null)
            return new PairDeviceResult(false, Error: error ?? "That sync space is not ready yet.");

        if (request.Mode is SyncMode.EncryptedReceiveOnly && string.IsNullOrWhiteSpace(request.EncryptionPassword))
            return new PairDeviceResult(false, Error:
                "An encrypted-only device needs a password, or it would receive readable files.");

        var body = new
        {
            deviceID = device.EngineDeviceId,
            // Syncthing encrypts this folder's copy for this device when a password is set. Sent
            // per share, because the same folder is plaintext for trusted devices.
            encryptionPassword = request.EncryptionPassword ?? ""
        };

        var response = await SendAsync(HttpMethod.Post,
            $"/rest/config/folders/{Uri.EscapeDataString(space.EngineFolderId)}/devices",
            body, cancellationToken);

        if (!response.Succeeded) return new PairDeviceResult(false, Error: response.Error);

        // Accepted by US. The other end has to add this node too, and until it does nothing moves —
        // which is why this is reported rather than assumed.
        return new PairDeviceResult(true, AcceptedByPeer: false);
    }

    public async Task<SyncFolderStatusResult> GetFolderStatusAsync(
        Guid folderId, CancellationToken cancellationToken)
    {
        var space = await db.SyncSpaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == folderId, cancellationToken);

        if (space?.EngineFolderId is null)
            return new SyncFolderStatusResult(false, SyncSpaceStatus.Pending,
                Error: "This space has not been created in the sync engine yet.");

        var status = await GetAsync<JsonElement>(
            $"/rest/db/status?folder={Uri.EscapeDataString(space.EngineFolderId)}", cancellationToken);

        if (!status.Succeeded)
            return new SyncFolderStatusResult(false, SyncSpaceStatus.Error, Error: status.Error);

        var element = status.Value;
        var needFiles = ReadLong(element, "needFiles");
        var needBytes = ReadLong(element, "needBytes");
        var totalFiles = ReadLong(element, "localFiles");
        var totalBytes = ReadLong(element, "localBytes");
        var state = ReadString(element, "state") ?? "";

        var connections = await ListConnectionsAsync(cancellationToken);
        var members = await db.SyncSpaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.SyncSpaceId == folderId)
            .Join(db.SyncDevices.IgnoreQueryFilters().AsNoTracking(),
                m => m.SyncDeviceId, d => d.Id, (m, d) => d.EngineDeviceId)
            .ToListAsync(cancellationToken);

        var connected = connections.Count(c => c.Connected && members.Contains(c.EngineDeviceId));

        var conflicts = await db.SyncConflicts.IgnoreQueryFilters()
            .CountAsync(c => c.SyncSpaceId == folderId
                             && c.Resolution == SyncConflictResolution.Unresolved, cancellationToken);

        return new SyncFolderStatusResult(
            true,
            MapStatus(state, needFiles, conflicts, connected, members.Count, space.IsPaused),
            needFiles, needBytes, totalFiles, totalBytes,
            connected, members.Count, conflicts,
            ReadDate(element, "stateChanged"));
    }

    public async Task<SyncOperationResult> SetPausedAsync(
        Guid folderId, bool paused, CancellationToken cancellationToken)
    {
        var space = await db.SyncSpaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == folderId, cancellationToken);

        if (space?.EngineFolderId is null)
            return new SyncOperationResult(false, "That sync space is not in the engine.");

        var response = await SendAsync(HttpMethod.Patch,
            $"/rest/config/folders/{Uri.EscapeDataString(space.EngineFolderId)}",
            new { paused }, cancellationToken);

        return response.Succeeded
            ? new SyncOperationResult(true)
            : new SyncOperationResult(false, response.Error);
    }

    public async Task<SyncOperationResult> UnpairDeviceAsync(
        PairSyncDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (space, device, error) = await LoadPairAsync(request, cancellationToken);
        if (space?.EngineFolderId is null || device is null)
            return new SyncOperationResult(false, error ?? "That pairing does not exist.");

        var response = await SendAsync(HttpMethod.Delete,
            $"/rest/config/folders/{Uri.EscapeDataString(space.EngineFolderId)}/devices/{Uri.EscapeDataString(device.EngineDeviceId)}",
            null, cancellationToken);

        // Note what this does NOT do: the removed device keeps the files it already has. Sync is not
        // remote wipe, and pretending otherwise would be a dangerous thing for a UI to imply.
        return response.Succeeded
            ? new SyncOperationResult(true)
            : new SyncOperationResult(false, response.Error);
    }

    /// <summary>
    /// Conflicting copies, found by their filenames.
    ///
    /// <para>
    /// Syncthing does not expose a conflicts endpoint; the conflict IS the file it wrote next to the
    /// original. So the folder is walked for the marker, which also means a conflict resolved by
    /// hand outside Harbora simply stops appearing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SyncConflictFile>> ListConflictsAsync(
        Guid folderId, CancellationToken cancellationToken)
    {
        var space = await db.SyncSpaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == folderId, cancellationToken);

        if (space is null || !Directory.Exists(space.LocalPath)) return [];

        var found = new List<SyncConflictFile>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                space.LocalPath, $"*{SyncConflictName.Marker}*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(space.LocalPath, path).Replace('\\', '/');
                var parsed = SyncConflictName.Parse(relative);
                if (parsed is null) continue;

                var info = new FileInfo(path);
                found.Add(new SyncConflictFile(
                    relative,
                    parsed.Value.OriginalPath,
                    info.Exists ? info.Length : 0,
                    parsed.Value.At ?? (info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow),
                    parsed.Value.Device));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be walked is worth a log, not an exception into a status page.
            logger.LogWarning(ex, "The sync folder {Path} could not be scanned for conflicts.", space.LocalPath);
        }

        return found;
    }

    public async Task<IReadOnlyList<SyncDeviceConnection>> ListConnectionsAsync(
        CancellationToken cancellationToken)
    {
        var response = await GetAsync<JsonElement>("/rest/system/connections", cancellationToken);
        if (!response.Succeeded) return [];

        if (!response.Value.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Object)
            return [];

        var results = new List<SyncDeviceConnection>();
        foreach (var entry in connections.EnumerateObject())
        {
            var address = ReadString(entry.Value, "address");
            var connected = entry.Value.TryGetProperty("connected", out var c)
                            && c.ValueKind == JsonValueKind.True;

            // Syncthing marks a relayed connection in its connection type string. Worth surfacing:
            // a relay means the bytes travel through a third party's server.
            var type = ReadString(entry.Value, "type") ?? "";
            var kind = type.Contains("relay", StringComparison.OrdinalIgnoreCase)
                ? SyncConnectionKind.Relay
                : type.Length > 0 ? SyncConnectionKind.Direct : SyncConnectionKind.Unknown;

            results.Add(new SyncDeviceConnection(
                entry.Name, connected, kind, address,
                ReadDate(entry.Value, "at"), ReadString(entry.Value, "clientVersion")));
        }

        return results;
    }

    public async Task<string?> GetLocalDeviceIdAsync(CancellationToken cancellationToken)
    {
        var response = await GetAsync<JsonElement>("/rest/system/status", cancellationToken);
        return response.Succeeded ? ReadString(response.Value, "myID") : null;
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<(SyncSpace? Space, SyncDevice? Device, string? Error)> LoadPairAsync(
        PairSyncDeviceRequest request, CancellationToken ct)
    {
        var space = await db.SyncSpaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.FolderId, ct);
        if (space is null) return (null, null, "That sync space no longer exists.");

        var device = await db.SyncDevices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null) return (space, null, "That device no longer exists.");

        return (space, device, null);
    }

    /// <summary>Syncthing's folder type strings, which are its vocabulary for our modes.</summary>
    private static string FolderType(SyncMode mode) => mode switch
    {
        SyncMode.SendOnly => "sendonly",
        SyncMode.ReceiveOnly => "receiveonly",
        SyncMode.EncryptedReceiveOnly => "receiveencrypted",
        _ => "sendreceive"
    };

    private static object Versioning(SyncVersioningMode mode, int parameter) => mode switch
    {
        SyncVersioningMode.Trash => new
        {
            type = "trashcan",
            @params = new Dictionary<string, string> { ["cleanoutDays"] = parameter.ToString() }
        },
        SyncVersioningMode.Simple => new
        {
            type = "simple",
            @params = new Dictionary<string, string> { ["keep"] = parameter.ToString() }
        },
        SyncVersioningMode.Staggered => new
        {
            type = "staggered",
            @params = new Dictionary<string, string> { ["maxAge"] = (parameter * 86400).ToString() }
        },
        _ => new { type = "" }
    };

    private static SyncSpaceStatus MapStatus(
        string state, long needFiles, int conflicts, int connected, int total, bool paused)
    {
        if (paused) return SyncSpaceStatus.Paused;
        if (state.Contains("error", StringComparison.OrdinalIgnoreCase)) return SyncSpaceStatus.Error;

        // Conflicts outrank "up to date". A folder can be perfectly in sync and still hold two
        // versions of somebody's document, and reporting that as fine is how it goes unnoticed.
        if (conflicts > 0) return SyncSpaceStatus.HasConflicts;

        if (needFiles > 0)
            return connected == 0 && total > 0
                ? SyncSpaceStatus.WaitingForDevices
                : SyncSpaceStatus.Syncing;

        return SyncSpaceStatus.UpToDate;
    }

    private sealed record ApiResult<T>(bool Succeeded, T Value, string? Error);

    private async Task<ApiResult<JsonElement>> GetAsync<T>(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new ApiResult<JsonElement>(false, default, "No Syncthing API key is configured.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-API-Key", _options.ApiKey);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new ApiResult<JsonElement>(false, default,
                    $"Syncthing returned {(int)response.StatusCode}.");

            var element = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return new ApiResult<JsonElement>(true, element, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Syncthing GET {Path} failed.", path);
            return new ApiResult<JsonElement>(false, default, Describe(ex));
        }
    }

    private async Task<ApiResult<bool>> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new ApiResult<bool>(false, false,
                "No Syncthing API key is configured, so Harbora cannot reach the sync engine.");

        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-API-Key", _options.ApiKey);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new ApiResult<bool>(true, true, null);

            var detail = await response.Content.ReadAsStringAsync(ct);
            return new ApiResult<bool>(false, false,
                $"Syncthing returned {(int)response.StatusCode}. {Truncate(detail)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Syncthing {Method} {Path} failed.", method, path);
            return new ApiResult<bool>(false, false, Describe(ex));
        }
    }

    /// <summary>
    /// An unreachable daemon is the common failure and deserves a sentence an operator can act on,
    /// rather than a socket exception surfacing through six layers.
    /// </summary>
    private string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => $"Syncthing did not answer within {_options.RequestTimeout.TotalSeconds:0}s.",
        HttpRequestException => $"Syncthing could not be reached at {_options.BaseUrl}. Is it running?",
        _ => "Syncthing returned something Harbora could not read."
    };

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= 300 ? value.Trim() : value[..300].Trim() + "…";

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        ReadString(element, property) is { } text
        && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
