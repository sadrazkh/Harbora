namespace Harbora.Infrastructure.Storage;

/// <summary>
/// Where the platform's object storage lives.
///
/// Empty by default, and the storage pages say so rather than offering a button that fails. Object
/// storage is a service an operator has to run — a MinIO on the host, or somebody else's S3 — and
/// pretending otherwise would produce a bucket row for a bucket that does not exist.
/// </summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage:S3";

    /// <summary>
    /// The S3 endpoint the platform administers, e.g. <c>http://harbora-minio:9000</c>. Empty means
    /// object storage is not set up on this installation.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// What customers should point their clients at, when that differs from the endpoint the panel
    /// uses. The panel usually reaches MinIO on a private network name nobody outside can resolve,
    /// and handing that out as the endpoint gives people an address that cannot work.
    /// </summary>
    public string PublicEndpoint { get; set; } = string.Empty;

    /// <summary>The administrative access key. Creates buckets and users; never shown to a tenant.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The administrative secret, from configuration or an environment variable.</summary>
    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// The image the administrative client runs from. The storage server's own image ships
    /// <c>mc</c>, so this is the same pin the catalogue already carries and there is no second
    /// image to keep current.
    /// </summary>
    public string ClientImage { get; set; } =
        "quay.io/minio/minio@sha256:9535594ad4122b7a78c6632788a989b96d9199b483d3bd71a5ceae73a922cdfa";

    /// <summary>
    /// The Docker network the helper joins. The storage server's name only resolves there; without
    /// it the helper dials a name that does not exist and the failure reads as bad credentials.
    /// </summary>
    public string Network { get; set; } = "harbora";

    /// <summary>Whether anything here is usable at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>The address to give a customer: the public one when set, the internal one otherwise.</summary>
    public string CustomerEndpoint =>
        string.IsNullOrWhiteSpace(PublicEndpoint) ? Endpoint : PublicEndpoint;

    /// <summary>
    /// What is missing, for the screen. Null when nothing is.
    ///
    /// Named individually because "storage is not configured" sends an operator to the
    /// documentation, and "Storage:S3:SecretKey is empty" does not.
    /// </summary>
    public string? WhatIsMissing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Endpoint)) missing.Add($"{SectionName}:Endpoint");
        if (string.IsNullOrWhiteSpace(AccessKey)) missing.Add($"{SectionName}:AccessKey");
        if (string.IsNullOrWhiteSpace(SecretKey)) missing.Add($"{SectionName}:SecretKey");

        return missing.Count == 0 ? null : string.Join(", ", missing);
    }
}
