using System.Text.Json;

namespace Harbora.Cli;

/// <summary>
/// One row of an app's effective environment, exactly as <c>GET apps/{slug}/env</c> describes it —
/// the wire shape of <c>Harbora.Domain.Apps.EffectiveEnvironmentEntry</c>, carried as plain JSON
/// rather than a shared assembly reference (the CLI ships as a standalone, self-contained binary and
/// does not reference the server's projects).
/// </summary>
public sealed record EffectiveEnvEntry(string Key, string Value, bool IsSecret, string Source);

/// <summary>
/// Fetches an app's effective environment from the panel — the one thing <c>harbora env pull</c> and
/// <c>harbora run</c> both need, and the only place either of them touches that endpoint.
///
/// <para>
/// Deliberately does no merging, precedence, or decryption of its own: "effective" means exactly what
/// <c>Harbora.Domain.Apps.ConfigGroupMerge</c> computes on the server, already decrypted for the one
/// endpoint that hands secrets back in plaintext (<c>ApiV1Controller.Env</c>'s own doc explains why).
/// If the CLI recomputed any part of that merge itself, it would drift from what a deploy actually
/// injects the first time the two implementations disagreed about anything — precedence, an
/// attachment's env var names, a database alias — and nothing would say so until a "works with
/// harbora run" app failed to work once deployed.
/// </para>
/// </summary>
public static class EffectiveEnv
{
    public static async Task<IReadOnlyList<EffectiveEnvEntry>> FetchAsync(
        ApiClient api, string slug, CancellationToken ct = default)
    {
        var payload = await api.GetAsync($"apps/{slug}/env", ct);
        var entries = new List<EffectiveEnvEntry>();
        foreach (var e in payload.EnumerateArray())
        {
            entries.Add(new EffectiveEnvEntry(
                e.GetProperty("key").GetString() ?? "",
                e.GetProperty("value").GetString() ?? "",
                e.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True,
                e.TryGetProperty("source", out var source) ? source.GetString() ?? "" : ""));
        }
        return entries;
    }
}
