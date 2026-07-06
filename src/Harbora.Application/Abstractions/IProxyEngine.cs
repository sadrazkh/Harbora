using Harbora.Domain.Networking;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Compiles routing rules into the reverse-proxy's dynamic config (Traefik) and applies
/// them safely: generate → validate → write → verify → (rollback on failure).
/// The visual route designer only ever produces <see cref="Route"/>s; it never edits config.
/// </summary>
public interface IProxyEngine
{
    /// <summary>Render the dynamic config for a set of routes without writing it (for preview).</summary>
    ProxyConfigPreview Preview(IReadOnlyList<Route> routes);

    /// <summary>Validate a rendered config for structural/logical errors before apply.</summary>
    ProxyValidationResult Validate(IReadOnlyList<Route> routes);

    /// <summary>
    /// Atomically apply routes. Writes the new config, keeps a backup, and rolls the file
    /// back if the proxy fails to pick it up. Traefik hot-reloads — no restart required.
    /// </summary>
    Task<ProxyApplyResult> ApplyAsync(IReadOnlyList<Route> routes, CancellationToken ct);
}

public record ProxyConfigPreview(string Format, string Content);
public record ProxyValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public record ProxyApplyResult(bool Success, string? Error, bool RolledBack);
