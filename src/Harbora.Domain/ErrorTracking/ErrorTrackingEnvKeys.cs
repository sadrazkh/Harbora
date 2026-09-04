namespace Harbora.Domain.ErrorTracking;

/// <summary>
/// The env var name an error-tracking attach hands an application (1.8, 2026-09 market-gaps round
/// two).
///
/// <para>
/// <c>SENTRY_DSN</c> is not a convention Harbora invented: it is the exact name every official
/// Sentry SDK (and every SDK for a Sentry-API-compatible server, GlitchTip included) reads on its own
/// when initialised with no explicit DSN argument — <c>Sentry.init()</c> in Node, <c>sentry_sdk.init()</c>
/// in Python, <c>SentrySdk.Init()</c> in .NET all fall back to it. An app that already has Sentry
/// wired up, or one copy-pasted from a runtime's own quick-start, is more likely to already read it.
/// </para>
/// </summary>
public static class ErrorTrackingEnvKeys
{
    public const string Dsn = "SENTRY_DSN";

    /// <summary>
    /// The one variable an attached error-tracking provider contributes. <paramref name="dsnCiphertext"/>
    /// is passed through unchanged — this method never touches <c>ISecretProtector</c> itself, exactly
    /// the split <see cref="Harbora.Domain.Email.EmailProviderEnvKeys.EntriesFor"/> already draws for
    /// SMTP: whoever assembles the final container environment (or renders a page that actually needs
    /// the plaintext) is the one place that calls <c>ISecretProtector.Unprotect</c>, once. A caller
    /// that decrypts before handing a value in here would have it decrypted a second time wherever
    /// <see cref="Apps.ConfigGroupMerge.Merge"/>'s own <c>IsSecret</c> entries are unprotected — which
    /// fails silently, since a plaintext string is not valid ciphertext.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(string dsnCiphertext) =>
    [
        (Dsn, dsnCiphertext, true)
    ];
}
