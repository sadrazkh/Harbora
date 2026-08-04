namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// What to keep when a host report leaves a field out.
///
/// Two callers write the same host facts — the collector on its tick and the server page on a manual
/// check — and an agent that does not report a field sends null. Assigning that null erases what an
/// earlier report told us, so the value flickers between known and unknown depending on which ran
/// last. Anything deciding on it, such as whether an image can run here, then answers differently
/// minute to minute for no visible reason.
/// </summary>
public static class ReportedFact
{
    /// <summary>The newly reported value, or the one already held when nothing was reported.</summary>
    public static string? Keep(string? current, string? reported) =>
        string.IsNullOrWhiteSpace(reported) ? current : reported.Trim();
}
