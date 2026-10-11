using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The <see cref="ServiceRefusal.Field"/> values the database services put on refusals that are not
/// about a form field, so a caller can tell refusals apart without matching on their text — which is
/// how <c>DatabasesController</c> keeps answering exactly as it did before the logic moved out of it
/// (a logical database that is not this instance's is a 404; a cross-network attach goes back to the
/// database's own page).
/// </summary>
public static class DatabaseRefusalFields
{
    /// <summary>The database itself: missing, not the caller's to manage, a replica, or still in use.</summary>
    public const string Service = "ServiceId";

    /// <summary>The app: missing, or not the caller's to change.</summary>
    public const string App = "AppId";

    /// <summary>The named logical database is not one of this instance's.</summary>
    public const string Database = "DatabaseId";

    /// <summary>The app and the database sit on different environments, so different private networks.</summary>
    public const string Environment = "EnvironmentId";
}

/// <summary>Refusals more than one database operation gives, worded once.</summary>
public static class DatabaseRefusals
{
    /// <summary>
    /// Not there, or not the caller's to manage — one answer for both, deliberately: telling someone
    /// which of the two it is tells them a database exists.
    /// </summary>
    public static readonly ServiceRefusal ServiceNotFound = new(
        "That database does not exist, or you are not allowed to manage it.",
        "این دیتابیس وجود ندارد یا اجازهٔ مدیریت آن را ندارید.",
        DatabaseRefusalFields.Service);

    /// <summary>The same rule for the app on the other end of an attach or detach.</summary>
    public static readonly ServiceRefusal AppNotFound = new(
        "That app does not exist, or you are not allowed to change its environment.",
        "این اپ وجود ندارد یا اجازهٔ تغییر متغیرهای محیطی آن را ندارید.",
        DatabaseRefusalFields.App);
}
