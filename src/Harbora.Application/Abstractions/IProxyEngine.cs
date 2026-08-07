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
    /// Atomically publish the routing for the <b>whole platform</b>. Writes the new config, keeps a
    /// backup, and rolls the file back if anything refuses. Traefik hot-reloads — no restart required.
    ///
    /// <para>
    /// It takes no routes, and that is the guarantee. The dynamic-config file is one file per
    /// install, so whatever is handed in <i>replaces</i> everything Harbora routes — and every caller
    /// used to hand in its own workspace's routes, which withdrew every other tenant's routing until
    /// somebody else re-applied and withdrew the first one's. Reading the set here is what makes
    /// "apply a subset" unsayable.
    /// </para>
    /// </summary>
    Task<ProxyApplyResult> ApplyAllAsync(CancellationToken ct);
}

public record ProxyConfigPreview(string Format, string Content);
public record ProxyValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public record ProxyApplyResult(bool Success, string? Error, bool RolledBack);
