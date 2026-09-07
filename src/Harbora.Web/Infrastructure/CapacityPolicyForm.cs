using System.Globalization;
using Harbora.Domain.Servers;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Parsing and validation for the capacity-policy form, shared by the two places that offer it.
///
/// <para>
/// There are two because a server and a node are not the same row. <c>NodesController</c> reaches a
/// server <i>through</i> an attached <c>Node</c>; <c>ServersController</c> reaches it directly, which
/// is the only way to reach the <b>Local</b> server at all — <c>DbSeeder</c> creates that one and
/// nothing ever gives it a <c>Node</c> row, so before this existed a single-server install had no way
/// to change these values while the panel told the operator they were an administrator's decision.
/// </para>
///
/// <para>
/// Shared rather than copied so the two entry points cannot drift into disagreeing about what a legal
/// policy is — which would be worse than the gap it closes, because one of them would be quietly wrong.
/// </para>
/// </summary>
public static class CapacityPolicyForm
{
    /// <summary>
    /// Parsed with <see cref="CultureInfo.InvariantCulture"/>, deliberately, and the fields bind as
    /// strings to make that possible.
    ///
    /// <para>
    /// The panel's default request culture is Persian, and ASP.NET Core's model binder parses numeric
    /// types with the request's culture — whose decimal separator is not ".". Binding these as
    /// <c>double</c> would turn a legitimate "2.5" into 0 and then refuse it as out of range. The form
    /// renders them with the invariant culture and <c>dir="ltr"</c> as the technical tokens they are,
    /// so they must come back the same way.
    /// </para>
    /// </summary>
    public static bool TryParseInvariant(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// The three values together, or the first reason they are not a legal policy — bilingual,
    /// because this reaches the operator as an in-page banner.
    /// </summary>
    /// <returns>Null when the policy is valid.</returns>
    public static string? Validate(double reservedMemoryRatio, double cpuFactor, double memFactor, bool isFa)
    {
        if (!ServerCapacityPolicy.IsValidReservedMemoryRatio(reservedMemoryRatio))
            return isFa
                ? $"سهم رزرو حافظه باید بین ۰٪ و {ServerCapacityPolicy.MaxReservedMemoryRatio * 100:0}٪ باشد."
                : $"Reserved memory must be between 0% and {ServerCapacityPolicy.MaxReservedMemoryRatio * 100:0}%.";

        if (!ServerCapacityPolicy.IsValidOvercommitFactor(cpuFactor, ServerCapacityPolicy.MaxCpuOvercommitFactor))
            return isFa
                ? $"ضریب مازاد-تعهد CPU باید بین {ServerCapacityPolicy.MinOvercommitFactor:0.#}× و {ServerCapacityPolicy.MaxCpuOvercommitFactor:0.#}× باشد."
                : $"The CPU overcommit factor must be between {ServerCapacityPolicy.MinOvercommitFactor:0.#}× and {ServerCapacityPolicy.MaxCpuOvercommitFactor:0.#}×.";

        // Deliberately a different ceiling from CPU's, and said so separately: memory overcommit fails
        // by OOM-kill where CPU contention only queues, so one message covering both would flatten the
        // difference the two limits exist to express.
        if (!ServerCapacityPolicy.IsValidOvercommitFactor(memFactor, ServerCapacityPolicy.MaxMemoryOvercommitFactor))
            return isFa
                ? $"ضریب مازاد-تعهد حافظه باید بین {ServerCapacityPolicy.MinOvercommitFactor:0.#}× و {ServerCapacityPolicy.MaxMemoryOvercommitFactor:0.#}× باشد."
                : $"The memory overcommit factor must be between {ServerCapacityPolicy.MinOvercommitFactor:0.#}× and {ServerCapacityPolicy.MaxMemoryOvercommitFactor:0.#}×.";

        return null;
    }

    public static string FormatGb(long bytes) =>
        (bytes / 1024.0 / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture);
}
