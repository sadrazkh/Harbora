using System.Security.Cryptography;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// The per-subscription key an <c>EventSubscription</c>'s webhook payloads are signed with (P6,
/// 2026-08-20 platform-options plan).
///
/// <para>
/// Mints the same way <c>Functions.FunctionInvokeSecret.Mint()</c> does — cryptographically random
/// bytes, hex-encoded — because that is already this codebase's one idiom for "a bearer secret shown
/// to an owner once, encrypted at rest with <c>ISecretProtector</c> everywhere else". 256 bits rather
/// than that helper's 192: this key does not travel in a header for equality comparison, it is the
/// HMAC-SHA256 key itself, and the extra 64 bits cost nothing.
/// </para>
/// </summary>
public static class EventSubscriptionSecret
{
    public static string Mint() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
