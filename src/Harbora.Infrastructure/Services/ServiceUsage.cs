namespace Harbora.Infrastructure.Services;

/// <summary>
/// Whether a service's environment points at a particular managed database.
///
/// Attaching a database writes its host into the app's environment — `PGHOST`, and the host inside
/// `DATABASE_URL` — and those variables are stored **encrypted**, because they carry the password.
/// That is the trap this exists to close: a screen that searched the stored values for the container
/// name was searching ciphertext, so it found nothing and reported an app with a database attached
/// as having no connections at all. The values have to be decrypted before they are asked anything.
///
/// The host is the signal rather than the variable name, because the names differ per engine and an
/// app is free to rename them.
/// </summary>
public static class ServiceUsage
{
    /// <summary>
    /// True when this (already decrypted) value refers to the given container.
    /// </summary>
    public static bool Mentions(string? decryptedValue, string? containerName) =>
        !string.IsNullOrWhiteSpace(decryptedValue)
        && !string.IsNullOrWhiteSpace(containerName)
        && decryptedValue.Contains(containerName, StringComparison.OrdinalIgnoreCase);
}
