using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// What a screen can honestly tell a developer about an attach, without showing them a password.
///
/// Attaching a database is one click, and then the developer is on their own: nothing said which
/// variables had appeared, so the answer was "read the source of ServiceCatalog, or guess". A .NET
/// developer guessed wrong in the only way that costs a whole afternoon — their application read
/// <c>ConnectionStrings:Default</c>, the attach wrote <c>PGHOST</c>, and the deployment died at the
/// health check with a stack trace about Npgsql that named neither the database nor the attach.
///
/// The names are read back out of <see cref="ServiceCatalog"/> rather than listed here. A second
/// list is a second thing to forget, and the failure it produces — a screen confidently naming a
/// variable nothing sets — is worse than saying nothing.
/// </summary>
public static class AttachGuidance
{
    /// <summary>The engine-native connection string, in the keyword form ADO.NET providers parse.</summary>
    public const string DsnKey = "DATABASE_DSN";

    /// <summary>
    /// The same value under the name .NET's configuration finds by itself: environment variables
    /// are read into <c>IConfiguration</c> with <c>__</c> mapped to <c>:</c>, so this overrides
    /// <c>ConnectionStrings:DefaultConnection</c> with no code change and no secret in a file.
    /// </summary>
    public const string DotNetKey = "ConnectionStrings__DefaultConnection";

    /// <summary>
    /// Placeholder credentials. Only the keys of the result are ever used — the point is to ask the
    /// catalog which names it writes, not to build a connection out of them.
    /// </summary>
    private static readonly ServiceCreds Probe = new("host", 0, "user", "password", "database");

    /// <summary>The variable names an attach of this type writes. Never any of the values.</summary>
    public static IReadOnlyList<string> KeysFor(ManagedServiceType type) =>
        ServiceCatalog.All.TryGetValue(type, out var definition)
            ? definition.AttachEnv(Probe).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// Whether an application on .NET has nothing left to do after attaching this — true for the
    /// engines with an ADO.NET provider, false for Redis and the brokers, which are reached by URL
    /// and have no <c>ConnectionStrings</c> key anybody would bind them to.
    /// </summary>
    public static bool WritesConnectionString(ManagedServiceType type) =>
        KeysFor(type).Contains(DotNetKey);
}
