namespace Harbora.Web.ViewModels;

/// <summary>
/// Why a server or a tier cannot be chosen.
///
/// <para>
/// A code rather than a sentence, so the builder that works it out needs no language and the partial
/// that draws it can say it in both. And a code rather than a bare <c>bool</c>, because "you cannot
/// have this" without saying why is the control this panel refuses to draw: the whole reason a
/// disabled card is better than a hidden one is that it explains itself.
/// </para>
/// </summary>
public enum SizeUnavailable
{
    /// <summary>It can be chosen.</summary>
    None = 0,

    /// <summary>The host is not connected, so nothing can be placed on it right now.</summary>
    ServerOffline,

    /// <summary>The host has no room left for this tier's memory or processor reservation.</summary>
    NoCapacity,

    /// <summary>This host does not offer this tier — the provider withdrew it here.</summary>
    NotOfferedHere,

    /// <summary>The workspace's plan does not include this tier.</summary>
    NotInPlan,

    /// <summary>
    /// Nobody has priced this tier on this host, and billing is switched on.
    ///
    /// <para>
    /// Shown and refused rather than hidden. Hidden, a priced-nowhere tier is capacity the operator
    /// cannot see they are failing to sell; selectable, it is capacity a customer takes for nothing —
    /// and the creation gate would refuse it anyway, one click later and with less explanation.
    /// </para>
    /// </summary>
    NotPriced,

    /// <summary>Every tier on this host is unavailable for one of the reasons above.</summary>
    NothingOffered
}

/// <summary>
/// Everything the shared size chooser needs: which hosts a customer may place on, which tiers each
/// host offers, and what each costs per hour and per month.
///
/// <para>
/// One model and one partial because the same three questions are asked on four screens — the
/// application form, the app resize control, the database resize control and the template deploy
/// form. Those are the same four places <c>InstanceSizeLabel</c> was written for after each had grown
/// its own version of one line.
/// </para>
/// </summary>
/// <param name="SizeFieldName">
/// The form field the chosen tier's key is posted as, so the partial does not have to assume every
/// caller named it the same thing. Today they all post <c>instanceSizeKey</c>; the parameter is what
/// stops the fifth one being wired up by editing the partial.
/// </param>
/// <param name="ServerFieldName">
/// The field the chosen host is posted as, or null when this caller does not let the host be chosen —
/// a resize keeps the workload where it is, because moving it is a different, destructive operation
/// with its own confirmation screen.
/// </param>
/// <param name="AllowNoLimit">
/// Whether "no ceiling" is offered as a choice. True on the resize controls, where it is the state a
/// resource created before tiers existed is already in, and false on creation, where choosing it
/// would hand out an unmetered workload.
/// </param>
public sealed record SizePickerModel(
    string SizeFieldName,
    string? ServerFieldName,
    string? SelectedSizeKey,
    Guid? SelectedServerId,
    bool AllowNoLimit,
    bool BillingEnabled,
    List<SizePickerServerViewModel> Servers)
{
    /// <summary>
    /// Whether there is a host worth drawing a chooser for. A single host with nothing to choose
    /// between still gets drawn — its tiers are the choice — but no host at all is an empty state,
    /// not an empty grid.
    /// </summary>
    public bool HasAnything => Servers.Count > 0;

    /// <summary>
    /// True when the customer has more than one host to weigh up, which is the only case where the
    /// host step earns its own row of cards.
    /// </summary>
    public bool ShowServerStep => ServerFieldName is not null && Servers.Count > 1;
}

/// <param name="Families">
/// What this host is optimised for, derived from the tiers it actually offers rather than stored on
/// the server. A badge kept in a column of its own could contradict the offers, and nothing would
/// report the disagreement.
/// </param>
public sealed record SizePickerServerViewModel(
    Guid ServerId,
    string Name,
    string Hostname,
    bool IsLocal,
    SizeUnavailable Unavailable,
    long FreeMemoryBytes,
    double FreeCpu,
    List<string> Families,
    List<SizePickerTierViewModel> Tiers)
{
    public bool Selectable => Unavailable == SizeUnavailable.None;

    /// <summary>
    /// The cheapest running hour anybody can actually buy here, for the host's own card — or null
    /// when nothing here is both selectable and priced.
    ///
    /// <para>
    /// Taken over the selectable tiers only. "From 0.01/hour" is a lie if the tier that costs a penny
    /// is one this customer's plan excludes or this host has withdrawn.
    /// </para>
    /// </summary>
    public long? FromRunningRateMinor => Tiers
        .Where(t => t.Selectable)
        .Select(t => t.RunningRateMinor)
        .OfType<long>()
        .Order()
        .Cast<long?>()
        .FirstOrDefault();
}

public sealed record SizePickerTierViewModel(
    string Key,
    string Name,
    string Family,
    double CpuCores,
    long MemoryBytes,
    long DiskBytes,
    long? RunningRateMinor,
    long? StoppedRateMinor,
    SizeUnavailable Unavailable)
{
    public bool Selectable => Unavailable == SizeUnavailable.None;
}
