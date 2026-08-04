using System.Net;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>Why a grant was refused, or null when it may go ahead.</summary>
public sealed record AccessRefusal(string Reason);

/// <summary>
/// The rules around opening a database to the outside.
///
/// Everything here exists because the failure it prevents is silent. An expired grant that is still
/// usable looks identical to a working one. A grant extended indefinitely was never temporary. An
/// allowlist with a typo in it either blocks the customer or, worse, allows everyone — and the
/// second only shows up in an incident review.
/// </summary>
public static class DatabaseAccessPolicy
{
    /// <summary>The offered windows. Anything else is a custom duration, bounded below.</summary>
    public static readonly IReadOnlyList<TimeSpan> Presets =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24)
    ];

    /// <summary>
    /// The longest a temporary grant may run for. Beyond this it is not temporary, and the person
    /// should be made to choose a persistent grant deliberately — with the warning that carries.
    /// </summary>
    public static readonly TimeSpan MaximumTemporary = TimeSpan.FromDays(7);

    /// <summary>Below this the grant expires before anybody can paste the connection string.</summary>
    public static readonly TimeSpan MinimumTemporary = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times a temporary grant may be extended. Unlimited extension is a permanent grant
    /// that nobody ever consciously approved.
    /// </summary>
    public const int MaximumExtensions = 3;

    /// <summary>Whether a requested duration is acceptable, and why not when it is not.</summary>
    public static AccessRefusal? RefuseDuration(TimeSpan duration)
    {
        if (duration < MinimumTemporary)
            return new AccessRefusal($"The shortest access window is {MinimumTemporary.TotalMinutes:0} minutes.");

        if (duration > MaximumTemporary)
            return new AccessRefusal(
                $"Temporary access cannot last longer than {MaximumTemporary.TotalDays:0} days. " +
                "Use persistent access if it needs to stay open.");

        return null;
    }

    /// <summary>
    /// Whether a grant may still be used right now.
    ///
    /// Asked at connection time as well as by the sweeper. The sweeper runs on a timer, so between
    /// two ticks an expired grant is still sitting there with a working password — this is what
    /// stops it being honoured in that gap.
    /// </summary>
    public static bool IsUsable(DatabaseAccessGrant grant, DateTimeOffset now)
    {
        if (grant.Status != DatabaseAccessStatus.Active) return false;
        if (grant.RevokedAt is not null) return false;

        // A persistent grant has no expiry; a temporary one without an expiry is a bug, and is
        // treated as expired rather than as eternal.
        if (grant.Kind == DatabaseAccessKind.Persistent) return true;

        return grant.ExpiresAt is { } expires && now < expires;
    }

    /// <summary>Grants the sweeper should close: past their time but still marked active.</summary>
    public static bool HasExpired(DatabaseAccessGrant grant, DateTimeOffset now) =>
        grant.Kind == DatabaseAccessKind.Temporary
        && grant.Status == DatabaseAccessStatus.Active
        && (grant.ExpiresAt is null || now >= grant.ExpiresAt);

    /// <summary>Whether the window may be pushed out, and why not when it may not.</summary>
    public static AccessRefusal? RefuseExtension(
        DatabaseAccessGrant grant, TimeSpan extension, DateTimeOffset now)
    {
        if (grant.Kind != DatabaseAccessKind.Temporary)
            return new AccessRefusal("Only temporary access can be extended.");

        if (!IsUsable(grant, now))
            return new AccessRefusal("That access has already ended. Create a new grant instead.");

        if (grant.ExtensionCount >= MaximumExtensions)
            return new AccessRefusal(
                $"This grant has already been extended {MaximumExtensions} times. Create a new one.");

        if (RefuseDuration(extension) is { } bad) return bad;

        // Measured from now, not from the original start: three extensions of a day each must not
        // add up to a grant that outlives the maximum.
        var newExpiry = now + extension;
        if (newExpiry - grant.CreatedAt > MaximumTemporary)
            return new AccessRefusal(
                $"That would take the total past {MaximumTemporary.TotalDays:0} days.");

        return null;
    }

    /// <summary>
    /// Whether a caller's address is allowed by the grant's allowlist.
    ///
    /// An empty list means anywhere. That is a real choice people make, so it is honoured — the
    /// interface is where it is spelled out, not here.
    /// </summary>
    public static bool AllowsAddress(string? allowedIps, IPAddress? caller)
    {
        if (string.IsNullOrWhiteSpace(allowedIps)) return true;
        if (caller is null) return false;

        foreach (var entry in allowedIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // An unparseable entry never matches. Treating it as "allow" would turn a typo in an
            // allowlist into an open door, which is the opposite of what the person asked for.
            if (Matches(entry, caller)) return true;
        }

        return false;
    }

    private static bool Matches(string entry, IPAddress caller)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0)
            return IPAddress.TryParse(entry, out var single) && single.Equals(caller);

        if (!IPAddress.TryParse(entry[..slash], out var network)) return false;
        if (!int.TryParse(entry[(slash + 1)..], out var prefix)) return false;

        var networkBytes = network.GetAddressBytes();
        var callerBytes = caller.GetAddressBytes();
        if (networkBytes.Length != callerBytes.Length) return false;
        if (prefix < 0 || prefix > networkBytes.Length * 8) return false;

        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (networkBytes[i] != callerBytes[i]) return false;

        var remainingBits = prefix % 8;
        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (callerBytes[fullBytes] & mask);
    }
}
