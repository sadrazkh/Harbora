namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Settings for delivering notifications.
///
/// Configurable for the same reason the health gate's timings are: a real ten-second wait belongs in
/// production and nowhere near a test suite, and a site whose channel is genuinely slow should be able
/// to say so rather than lose every alert.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// How long a channel has to accept a message. Bounded because whatever is reporting waits here:
    /// without it, a failed deploy sits through the HTTP handler's 100-second default — once per alert
    /// rule — before it can tell anyone it failed.
    /// </summary>
    public double DeliveryTimeoutSeconds { get; set; } = 10;

    internal TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(Math.Max(0.01, DeliveryTimeoutSeconds));
}
