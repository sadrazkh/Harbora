using Harbora.Domain.Common;

namespace Harbora.Domain.Email;

/// <summary>
/// A workspace's own SMTP account, used to send mail on behalf of the apps attached to it (F6,
/// 2026-08-21 functions-and-services plan — HARBORA-0038 phase 1).
///
/// Bring-your-own only: Harbora stores the credentials and hands them to an attached app as
/// <c>SMTP_*</c> env vars, exactly the way <see cref="Harbora.Domain.Storage.StorageBucket"/> hands
/// out S3 credentials (F5). There is no relay in front of it — the app's own container speaks SMTP
/// straight to <see cref="Host"/>. Running Harbora's own mail transfer agent is a different, later
/// project (HARBORA-0039); this row never represents one.
///
/// Distinct on purpose from <c>Harbora.Domain.Mail.MailDomain</c>/<c>MailServer</c>, which is a
/// separate, already-shipped feature: Harbora *hosting* mailboxes (<c>user@yourdomain.com</c>,
/// IMAP, billed per mailbox) on a shared Stalwart server the platform runs. Nothing here talks to
/// that server, provisions a mailbox, or bills by the hour — a row here is a credential for
/// somebody else's SMTP server, kept only so an app can be handed it.
/// </summary>
public class EmailProvider : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>What the workspace calls it — "SendGrid", "Office365" — shown instead of the host
    /// wherever a person picks one.</summary>
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;

    /// <summary>Not a secret on its own — an SMTP username is usually an email address or an API
    /// key's public half.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The password (or full API key used as one), encrypted with the platform key like
    /// every other stored credential.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>The address mail appears to come from. Required — a provider with nothing to put in
    /// "From" cannot send anything real, whatever the test button says.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>The display name beside <see cref="FromAddress"/>, e.g. "Acme Support". Optional.</summary>
    public string? FromName { get; set; }

    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// The last time somebody pressed the test-send button, and what the provider actually said —
    /// not a guess, and not cleared just because time passed. Null means never tested, which the
    /// page shows as "not tested" rather than as a fabricated pass or fail.
    /// </summary>
    public DateTimeOffset? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }

    /// <summary>The provider's own words on the last attempt — its accept, or its refusal, verbatim
    /// where possible. Null when never tested.</summary>
    public string? LastTestMessage { get; set; }

    public ICollection<AppEmailProvider> Apps { get; set; } = new List<AppEmailProvider>();
}
