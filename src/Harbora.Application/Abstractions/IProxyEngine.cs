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
    /// <param name="callerWorkspaceId">
    /// The workspace whose action triggered this apply, or <see langword="null"/> for a caller with
    /// no workspace of its own (a sessionless sweep). Validation runs over every route on the
    /// platform, and a route that fails it is left out of the render — it was never going to serve,
    /// and refusing the whole file for it means one tenant's bad row stops every tenant deploying.
    /// This parameter decides two things about that, both of them about the caller and neither about
    /// what is written:
    /// <list type="bullet">
    /// <item>Whether the apply is reported as a <b>failure</b>. Yes if one of the dropped routes
    /// belongs to this workspace — the caller can act on their own row, and a deployment whose own
    /// domain was left out has not deployed it. No if every dropped route belongs to somebody else:
    /// there is nothing here this caller can fix, and failing them for it is the outage.</item>
    /// <item>What <see cref="ProxyApplyResult.Error"/> may say. A route this workspace owns is named
    /// in full; a route belonging to any other workspace is not, because naming another tenant's
    /// hostname — and that it is misconfigured — to a caller who does not own it is a leak, not a
    /// diagnostic.</item>
    /// </list>
    /// The full detail, every route named, always reaches the server log and the rendered file's own
    /// comments regardless of who called.
    /// </param>
    Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct);
}

public record ProxyConfigPreview(string Format, string Content);
public record ProxyValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public record ProxyApplyResult(bool Success, string? Error, bool RolledBack);
