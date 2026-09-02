namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which image a PostgreSQL instance actually runs once pgvector support is turned on
/// (<see cref="Harbora.Domain.Services.ManagedService.PgVectorEnabled"/>, 1.7 pgvector-as-option
/// plan).
///
/// <para>
/// The stock <c>postgres</c> image <see cref="ServiceCatalog"/> otherwise runs carries no pgvector
/// files at all — <c>CREATE EXTENSION vector</c> against it fails with Postgres's own "could not open
/// extension control file", not a refusal Harbora invented. <c>pgvector/pgvector</c> is the
/// extension's own maintained image: the official PostgreSQL image plus pgvector precompiled and
/// nothing else changed, which is what makes swapping to it — rather than teaching Harbora to build
/// or patch an image itself — the honest way to make the extension available at all.
/// </para>
/// </summary>
public static class PgVectorImage
{
    /// <summary>
    /// The pgvector image tag for a PostgreSQL major version this catalogue offers
    /// (<c>ServiceCatalog.All[ManagedServiceType.PostgreSql].Versions</c>: "16-alpine", "15-alpine").
    /// Falls back to the newest major pgvector still ships a tag for rather than throwing, so a
    /// version string this mapping has not been told about yet degrades to "wrong minor, still boots"
    /// instead of a failed provision — the same reasoning <c>ManagedService.RunningImage</c>'s own doc
    /// gives for a moving tag.
    /// </summary>
    public static string For(string postgresVersion) => postgresVersion switch
    {
        "15-alpine" => "pgvector/pgvector:pg15",
        _ => "pgvector/pgvector:pg16"
    };
}
