using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>Why external access cannot be offered right now, or null when it can.</summary>
public sealed record AccessUnavailable(string Reason, string ReasonFa);

/// <summary>
/// Whether this installation can actually open a database to the outside.
///
/// A rule rather than an <c>if</c> in the controller, because the answer must be the same wherever
/// it is asked. The failure it prevents is the worst kind this feature could have: a page that
/// happily issues a username, a password and a connection string pointing at a gateway that does not
/// exist. The customer pastes it into their client, gets a name-resolution error, and reports a
/// broken database — while Harbora's own records show a healthy, active grant.
/// </summary>
public static class ExternalAccessAvailability
{
    /// <param name="canOpenLocally">
    /// True when Harbora can open the port itself, which it can on a single-server install. The
    /// node agent is only needed for the multi-server case, and for a long time its absence blocked
    /// a feature that never needed it here.
    /// </param>
    public static AccessUnavailable? Refuse(
        INodeAgentClient node, ManagedService? service, bool canOpenLocally = false)
    {
        if (service is not null && !DatabaseGrantSql.Supports(service.Type))
            return new AccessUnavailable(
                DatabaseGrantSql.UnsupportedReason(service.Type),
                $"دسترسی خارجی برای {service.Type} هنوز ساخته نشده. فعلاً PostgreSQL، MySQL و MariaDB.");

        if (!canOpenLocally && node.IsSimulated)
            return new AccessUnavailable(
                "External access needs the Harbora node agent, which is not configured on this installation. " +
                "Nothing would be reachable, so nothing is issued.",
                "دسترسی خارجی به agent نود نیاز دارد که روی این نصب پیکربندی نشده. " +
                "چیزی قابل اتصال نخواهد بود، پس چیزی هم صادر نمی‌شود.");

        if (service is null)
            return new AccessUnavailable("That database no longer exists.", "این دیتابیس دیگر وجود ندارد.");

        // Opening a stopped database hands out a credential for something nothing can connect to,
        // and the grant's clock starts running immediately.
        if (service.Status != ServiceStatus.Running)
            return new AccessUnavailable(
                "That database is not running. Start it before opening it to the outside.",
                "این دیتابیس در حال اجرا نیست. پیش از باز کردن آن به بیرون، اجرایش کنید.");

        return null;
    }
}
