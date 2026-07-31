using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>How a database's password is changed, engine by engine.</summary>
/// <param name="Command">Run inside a container of the database's own image, through a shell.</param>
/// <param name="Env">Carries the <b>current</b> password, so the command line never holds either one.</param>
public sealed record RotationPlan(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Rotating a database password.
///
/// The engines genuinely differ, and pretending otherwise is how a feature ends up half-working:
/// SQL engines change it live, Redis takes it on its own command line and can only be given a new
/// one by recreating the container, and MongoDB's tooling changed name between the versions in the
/// catalog — so it says it is not supported rather than shipping a command that works on one and
/// silently fails on the other.
/// </summary>
public static class CredentialRotationPlan
{
    /// <summary>The statement that changes the password, or null when this engine needs another route.</summary>
    public static RotationPlan? For(ManagedServiceType type, ServiceCreds current, string newPassword) => type switch
    {
        ManagedServiceType.PostgreSql => new RotationPlan(
            ["sh", "-c",
             $"psql -h {Shell(current.Host)} -p {current.Port} -U {Shell(current.User)} " +
             $"-d {Shell(current.Database)} -v ON_ERROR_STOP=1 " +
             $"-c {Shell($"ALTER USER {Identifier(current.User)} WITH PASSWORD {SqlString(newPassword)}")}"],
            new Dictionary<string, string> { ["PGPASSWORD"] = current.Password }),

        ManagedServiceType.MySql or ManagedServiceType.MariaDb => new RotationPlan(
            ["sh", "-c",
             $"mysql -h {Shell(current.Host)} -P {current.Port} -u {Shell(current.User)} " +
             $"-e {Shell($"ALTER USER {SqlString(current.User)}@'%' IDENTIFIED BY {SqlString(newPassword)}; FLUSH PRIVILEGES;")}"],
            new Dictionary<string, string> { ["MYSQL_PWD"] = current.Password }),

        _ => null
    };

    /// <summary>
    /// True when the only way to change the password is to start the container again with it. Redis
    /// reads it from its own command line, so there is nothing to alter while it runs.
    /// </summary>
    public static bool RequiresRecreate(ManagedServiceType type) => type is ManagedServiceType.Redis;

    /// <summary>
    /// Why this engine cannot be rotated yet, for the screen. Saying so is the point: a button that
    /// appears to work and does nothing is worse than one that is not offered.
    /// </summary>
    public static string? WhyUnsupported(ManagedServiceType type) => type switch
    {
        ManagedServiceType.MongoDb =>
            "MongoDB's shell changed name between the versions Harbora offers, so rotating its password " +
            "is not automated yet. Change it in the database and update the attached services by hand.",
        _ => null
    };

    /// <summary>
    /// Whether a password can be carried through a shell and a SQL statement without being mangled.
    /// Generated passwords are alphanumeric, and this is the check that keeps them that way rather
    /// than a comment saying they are.
    /// </summary>
    public static bool IsSafeToApply(string? password) =>
        !string.IsNullOrWhiteSpace(password)
        && password.Length >= 12
        && password.All(char.IsLetterOrDigit);

    /// <summary>Single-quotes a value for <c>sh -c</c>.</summary>
    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>A SQL string literal — doubled quotes, the SQL escape rather than the shell one.</summary>
    private static string SqlString(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>A quoted SQL identifier, where the escape is a doubled double-quote.</summary>
    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
