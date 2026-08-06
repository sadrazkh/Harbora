using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The rules of a throwaway database-admin container.
///
/// Pure, because every one of these decisions is a way to leave a door open. The tool is a web
/// interface with network reach into a customer's private network; it exists for minutes, behind a
/// password nobody chose, and it stops itself. Nothing here talks to Docker — that is the caller's
/// job, and this is what the caller must obey.
/// </summary>
public static class AdminerSession
{
    /// <summary>
    /// Pinned by digest, like every other image this platform runs. A tag moves; this is the exact
    /// build that was looked at. Read from the registry, never invented.
    /// </summary>
    public const string Image =
        "adminer@sha256:890cffec7caa20159fb6f68c1a521b2e5879f7314f4845d4ebca7cc1cf145971";

    /// <summary>
    /// How long a session lives. Long enough to do a piece of work, short enough that forgetting
    /// about it is not a standing exposure — and it is enforced by a sweeper, not by asking.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>The container name for one service's session. One per database, so a second click reuses it.</summary>
    public static string ContainerName(Guid serviceId) => $"harbora-adminer-{serviceId:N}"[..Math.Min(63, 16 + 32)];

    /// <summary>
    /// The engine name Adminer expects in its connection form. Unsupported engines are refused
    /// rather than offered a tool that cannot speak to them — a button that opens a page saying
    /// "unknown driver" is worse than no button.
    /// </summary>
    public static string? DriverFor(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql => "pgsql",
        ManagedServiceType.MySql => "server",
        ManagedServiceType.MariaDb => "server",
        _ => null
    };

    /// <summary>Whether this database can be administered with this tool at all.</summary>
    public static bool Supports(ManagedServiceType type) => DriverFor(type) is not null;

    /// <summary>
    /// Whether a session that started at <paramref name="startedAt"/> is past its life.
    /// The clock is a parameter for the usual reason: a rule that reads it cannot be tested at
    /// the boundary where it matters.
    /// </summary>
    public static bool Expired(DateTimeOffset startedAt, DateTimeOffset now) =>
        now - startedAt >= Lifetime;
}
