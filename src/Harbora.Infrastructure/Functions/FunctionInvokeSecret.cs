using System.Security.Cryptography;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// The shared secret the panel presents when it invokes a function itself.
///
/// <para>
/// One definition, because there are two places that mint one — a function app being created, and a
/// function app being cloned into another environment — and a second copy of "what a secret is" is
/// how one of them ends up shorter, or predictable, without anything failing.
/// </para>
///
/// <para>
/// It is never copied from one app to another. A staging copy holding production's secret would be a
/// staging copy that can fire production's schedules, which is the same failure a cloned database
/// password would be.
/// </para>
/// </summary>
public static class FunctionInvokeSecret
{
    /// <summary>192 bits, hex — long enough that guessing is not the attack, short enough for a header.</summary>
    public static string Mint() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
