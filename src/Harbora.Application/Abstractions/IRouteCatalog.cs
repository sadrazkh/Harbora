using Harbora.Domain.Networking;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Every route the platform is currently routing — the input to the one dynamic-config file that
/// describes them all.
///
/// <para>
/// It exists as its own seam for one reason: the proxy engine is a singleton and this read must be
/// <b>unscoped</b>. Under the tenant query filter a request thread reads only its own workspace and
/// a sessionless caller (a job, a webhook, a sweeper) reads nothing at all — and "nothing" rendered
/// into that file is not a no-op, it is the whole platform going dark. One method, one place to say
/// that, one place to test it.
/// </para>
/// </summary>
public interface IRouteCatalog
{
    /// <summary>Every enabled route in every workspace, in a stable order.</summary>
    Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct);
}
