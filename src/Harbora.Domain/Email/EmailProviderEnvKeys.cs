namespace Harbora.Domain.Email;

/// <summary>
/// The env var names a provider attach hands an application (F6, 2026-08-21 functions-and-services
/// plan).
///
/// <para>
/// Checked before inventing anything: no <c>SMTP_*</c> convention exists anywhere else in this
/// codebase (the platform's own outgoing mail, <c>PlatformMailer</c>, reads its settings from the
/// <c>Setting</c> table, not from env vars an app would see) and the plan itself names these exact
/// keys — "injected into attached apps as <c>SMTP_*</c> env vars" — matching what most SMTP
/// libraries and frameworks already expect by convention (Nodemailer, PHPMailer, Django's
/// <c>EMAIL_HOST</c> family), so an app copy-pasted from elsewhere is more likely to already read
/// them.
/// </para>
/// </summary>
public static class EmailProviderEnvKeys
{
    public const string Host = "SMTP_HOST";
    public const string Port = "SMTP_PORT";
    public const string User = "SMTP_USER";
    public const string Password = "SMTP_PASSWORD";
    public const string From = "SMTP_FROM";
    public const string Secure = "SMTP_SECURE";

    /// <summary>
    /// The six variables an attached provider contributes. <paramref name="passwordCiphertext"/> is
    /// passed through unchanged — this method never touches <c>ISecretProtector</c> itself, exactly
    /// the split <see cref="Harbora.Domain.Storage.BucketEnvKeys.EntriesFor"/> already draws for
    /// buckets: whoever assembles the final container environment (or renders a page that actually
    /// needs the plaintext) is the one place that calls <c>ISecretProtector.Unprotect</c>, once. A
    /// caller that decrypts before handing a value in here would have it decrypted a second time
    /// wherever <see cref="Apps.ConfigGroupMerge.Merge"/>'s own <c>IsSecret</c> entries are
    /// unprotected — which fails silently, since a plaintext string is not valid ciphertext.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(
        EmailProvider provider, string passwordCiphertext) =>
    [
        (Host, provider.Host, false),
        (Port, provider.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
        (User, provider.Username, false),
        (Password, passwordCiphertext, true),
        (From, FromHeader(provider), false),
        (Secure, provider.UseSsl ? "true" : "false", false)
    ];

    /// <summary>"Acme Support &lt;support@acme.example&gt;" when a display name was given; the bare
    /// address otherwise — the same fallback a mail client shows when a From header carries no
    /// name.</summary>
    public static string FromHeader(EmailProvider provider) =>
        string.IsNullOrWhiteSpace(provider.FromName)
            ? provider.FromAddress
            : $"{provider.FromName} <{provider.FromAddress}>";
}
