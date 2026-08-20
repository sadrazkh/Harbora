namespace Harbora.Web.ViewModels;

/// <summary>One row of the customer's own Cloudflare zone list.</summary>
public sealed record CustomerDnsZoneRow(string Id, string Name);

/// <summary>One DNS record of a type F9 manages (A, AAAA, CNAME, TXT, MX).</summary>
public sealed record CustomerDnsRecordRow(
    string Id, string Type, string Name, string Content, int Ttl, int? Priority, bool Proxied);

/// <summary>
/// The small summary shown on the Domains page itself — read from stored state only, never a live
/// Cloudflare call, so opening /domains never depends on a third party answering.
/// </summary>
public sealed record CustomerDnsSummaryViewModel
{
    public required bool HasToken { get; init; }
    public required DateTimeOffset? LastVerifiedAt { get; init; }
    public required string? LastVerificationError { get; init; }
}

/// <summary>
/// The full DNS-records page (/domains/dns). Exactly one of three shapes is true at a time, and the
/// view renders exactly one of them — never a table that could be mistaken for any of the others:
/// <list type="bullet">
/// <item><b>No token</b> (<see cref="HasToken"/> false): says what is needed, offers the add-token
/// form, nothing else.</item>
/// <item><b>Token, but zones could not be listed</b> (<see cref="ZonesError"/> set): the exact
/// Cloudflare/verification failure, not an empty zone list.</item>
/// <item><b>Token and zones</b>: a zone picker, and — once one is chosen — its records with add/delete
/// forms, or that zone's own <see cref="RecordsError"/> if listing them failed.</item>
/// </list>
/// </summary>
public sealed record CustomerDnsPageViewModel
{
    public required bool HasToken { get; init; }
    public required DateTimeOffset? LastVerifiedAt { get; init; }
    public required string? LastVerificationError { get; init; }

    public required IReadOnlyList<CustomerDnsZoneRow> Zones { get; init; }
    public required string? ZonesError { get; init; }

    public string? SelectedZoneId { get; init; }
    public string? SelectedZoneName { get; init; }
    public IReadOnlyList<CustomerDnsRecordRow>? Records { get; init; }
    public string? RecordsError { get; init; }
}
