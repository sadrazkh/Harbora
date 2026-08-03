namespace Harbora.Web;

/// <summary>
/// Marker type for the shared string catalog. The marker stays at the web root while localized
/// files live under Resources/, matching the configured ResourcesPath without duplicating the
/// folder segment in the localizer base name.
/// </summary>
public sealed class SharedResource;
