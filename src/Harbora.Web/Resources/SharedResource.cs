namespace Harbora.Web.Resources;

/// <summary>
/// Marker type for the shared string catalog. Views inject IStringLocalizer&lt;SharedResource&gt;
/// and use English text as keys; Persian translations live in SharedResource.fa.resx. Missing
/// keys fall back to the English key, so the UI is always fully localizable and never blank.
/// </summary>
public sealed class SharedResource;
