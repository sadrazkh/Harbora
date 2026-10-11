using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Storage;
using Harbora.Shared;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// The refusals the app services give, in both languages. Moved here from <c>AppsController</c> with
/// their wording intact — the English and, where there was one, the Persian are exactly what the form
/// and the TempData banner showed before; where the controller only ever had English, the Persian is new.
/// </summary>
internal static class AppRefusals
{
    /// <summary>
    /// The one answer for "not yours" and "not there", deliberately: telling someone which of the two it
    /// is tells them a resource exists (<c>ProjectAccessService.CanTouchAppAsync</c> answers both the
    /// same way for the same reason).
    /// </summary>
    public static readonly ServiceRefusal AppNotReachable = new(
        "That app does not exist, or you are not allowed to change it.",
        "این اپ وجود ندارد یا اجازهٔ تغییر آن را ندارید.");

    public static readonly ServiceRefusal NameRequired = new(
        "A name is required.", "نام لازم است.", nameof(AppCreateRequest.Name));

    /// <summary>
    /// A typed app slug that another workspace already holds. App slugs are unique across the whole
    /// platform (HarboraDbContext: <c>HasIndex(x => x.Slug).IsUnique()</c>) because a container is
    /// named from it and containers are listed host-wide — "that name is taken" on its own would be
    /// baffling here, since the person can see their own workspace and there is nothing called this
    /// in it.
    /// </summary>
    public static ServiceRefusal TakenSlug(string slug) => new(
        $"'{slug}' is already taken on this platform — app names are shared across every workspace, not just yours, so there being nothing called '{slug}' in your own workspace doesn't mean it's free.",
        $"«{slug}» قبلاً در پلتفرم استفاده شده است — نام اپ در کل پلتفرم مشترک است، نه فقط در این Workspace؛ در Workspace شما چیزی با این نام نیست.",
        nameof(AppCreateRequest.Slug));

    public static readonly ServiceRefusal CloneUrlRequired = new(
        "A Git repository URL is required.", "آدرس مخزن Git لازم است.", nameof(AppCreateRequest.CloneUrl));

    public static readonly ServiceRefusal ImageRequired = new(
        "An image reference is required.", "نام ایمیج (image) لازم است.", nameof(AppCreateRequest.PrebuiltImage));

    /// <summary>
    /// English from <see cref="AppRootDirectory.Explain"/>, as before. The Persian names the same value
    /// and the same rule, because the person has to be able to tell a typo from a rule in either language.
    /// </summary>
    public static ServiceRefusal RootDirectory(string? value, PathRejection why)
    {
        var shown = string.IsNullOrWhiteSpace(value) ? "(خالی)" : value.Trim();
        var fa = why switch
        {
            PathRejection.ParentTraversal =>
                $"پوشهٔ ریشهٔ «{shown}» با «..» به بیرون از مخزن اشاره می‌کند. باید پوشه‌ای داخل مخزن باشد، مثل api یا services/worker.",
            PathRejection.Rooted =>
                $"پوشهٔ ریشهٔ «{shown}» مسیر مطلق است. باید نسبت به ریشهٔ مخزن نوشته شود، مثل api یا services/worker.",
            PathRejection.EscapesRoot =>
                $"پوشهٔ ریشهٔ «{shown}» به بیرون از مخزن می‌رسد. باید پوشه‌ای داخل مخزن باشد، مثل api یا services/worker.",
            PathRejection.InvalidCharacter =>
                $"پوشهٔ ریشهٔ «{shown}» نویسه‌ای دارد که در مسیر نمی‌آید.",
            PathRejection.Empty =>
                "پوشهٔ ریشه خالی است. برای ساختن از ریشهٔ مخزن، آن را خالی بگذارید.",
            _ => $"پوشهٔ ریشهٔ «{shown}» پذیرفته نشد ({why})."
        };
        return new(AppRootDirectory.Explain(value, why), fa, nameof(AppCreateRequest.RootDirectory));
    }

    /// <summary>
    /// <see cref="Deployments.CronSchedule.TryParse"/> explains itself in English only, field by field;
    /// the Persian says what was refused and carries that explanation along rather than guessing at a
    /// translation of every case.
    /// </summary>
    public static ServiceRefusal CronExpression(string error) => new(
        error, $"زمان‌بندی پذیرفته نشد: {error}", nameof(AppCreateRequest.CronExpression));

    public static readonly ServiceRefusal CronCommandRequired = new(
        "Enter the command this job should run, for example \"php artisan backup:run\".",
        "فرمانی را که این کار باید اجرا کند بنویسید، مثلاً «php artisan backup:run».",
        nameof(AppCreateRequest.Command));

    public static readonly ServiceRefusal EnvironmentNotAllowed = new(
        "You do not have permission to create an app in this environment.",
        "اجازهٔ ساختن اپ در این محیط را ندارید.",
        nameof(AppCreateRequest.EnvironmentId));

    public static readonly ServiceRefusal TemplateUnavailable = new(
        "That template does not exist or is not available to this workspace.",
        "این قالب وجود ندارد یا برای این فضای کاری در دسترس نیست.",
        nameof(AppCreateRequest.TemplateId));

    public static ServiceRefusal TemplateUnreadable(string name) => new(
        $"The template '{name}' cannot be used: its manifest does not parse.",
        $"قالب «{name}» قابل استفاده نیست: مانیفست آن خوانده نمی‌شود.",
        nameof(AppCreateRequest.TemplateId));

    /// <summary>
    /// A quota refusal, carried in both languages. <see cref="QuotaCheck.ReasonFa"/> is optional — the
    /// plan-limit refusals written before it existed still have none — so the Persian side falls back to
    /// the English reason, exactly what a Persian page showed for them before.
    /// </summary>
    public static ServiceRefusal Quota(QuotaCheck check, string fallback, string fallbackFa) => new(
        check.Reason ?? fallback, check.ReasonFa ?? check.Reason ?? fallbackFa);

    public static ServiceRefusal Capacity(PlacementResult placement) => new(
        placement.Reason ?? "No server has capacity for this instance size.",
        placement.ReasonFa ?? placement.Reason ?? "هیچ سروری برای این اندازه جا ندارد.");

    public static ServiceRefusal ReservedHost(string host, string? field) => new(
        $"'{host}' is one of the platform's own host names and cannot be routed to an app.",
        $"«{host}» یکی از نام‌های خودِ سامانه است و نمی‌توان آن را به یک اپ داد.",
        field);

    /// <summary>The refusal a typed host that is already taken gets on the create form.</summary>
    public static ServiceRefusal TakenHost(string host) => new(
        $"'{host}' is already in use.", $"«{host}» پیش‌تر استفاده شده است.", nameof(AppCreateRequest.Domain));

    public static readonly ServiceRefusal EnvVariablesRequired = new(
        "Give at least one variable to set.", "دست‌کم یک متغیر برای تنظیم بدهید.", "Variables");

    public static readonly ServiceRefusal EnvKeyRequired = new(
        "Key is required.", "نام متغیر (Key) لازم است.", "Key");

    public static readonly ServiceRefusal EnvVariableNotFound = new(
        "That variable is not set on this app.", "این متغیر روی این اپ تنظیم نشده است.", "Key");

    public static readonly ServiceRefusal HostRequired = new(
        "Host is required.", "نام دامنه را بنویسید.", "Host");

    /// <summary>The domains form's own wording for a taken host, which has always differed from the create form's.</summary>
    public static readonly ServiceRefusal DomainInUse = new(
        "This domain is already in use.", "این دامنه پیش‌تر استفاده شده است.", "Host");

    public static readonly ServiceRefusal DomainNotFound = new(
        "That domain is not attached to this app.", "این دامنه به این اپ وصل نیست.", "Host");

    public static ServiceRefusal Mount(MountPathRefusal refusal) => refusal switch
    {
        MountPathRefusal.Missing => new("Type a path, for example /data", "مسیر را بنویسید، مثلاً /data", "MountPath"),
        MountPathRefusal.NotAbsolute => new(
            "The path must start with /. A relative one is resolved against the image's working directory, which is not visible from here.",
            "مسیر باید با / شروع شود؛ مسیر نسبی به پوشهٔ کاری ایمیج حساب می‌شود که معلوم نیست کجاست.",
            "MountPath"),
        MountPathRefusal.Unsafe => new(
            "That path contains something a path cannot contain.", "این مسیر نویسه‌های غیرمجاز دارد.", "MountPath"),
        MountPathRefusal.TooLong => new("That path is too long.", "مسیر خیلی بلند است.", "MountPath"),
        _ => new(
            "That directory belongs to the image. Mounting an empty volume over it replaces the image's own files with nothing, and the container does not start.",
            "این پوشه مال خود ایمیج است. سوار کردن والیوم خالی روی آن، محتوای ایمیج را با هیچ جایگزین می‌کند و کانتینر بالا نمی‌آید.",
            "MountPath")
    };

    public static ServiceRefusal AlreadyMounted(string mountPath) => new(
        $"{mountPath} is already mounted.", $"«{mountPath}» از قبل هست.", "MountPath");

    public static readonly ServiceRefusal VolumeNotFound = new(
        "That volume is not mounted on this app.", "این والیوم روی این اپ نیست.", "MountPath");

    public static ServiceRefusal VolumeProtected(VolumeProtectedException ex) => new(ex.Message, ex.ReasonFa, "MountPath");
}
