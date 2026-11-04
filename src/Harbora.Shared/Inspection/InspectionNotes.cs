namespace Harbora.Shared.Inspection;

/// <summary>
/// Every note the inspector can write, in both languages. Each one says what it means for this app
/// and what to do, because it is shown to somebody who has just given us their code and nothing else.
/// </summary>
internal static class InspectionNotes
{
    public static InspectionNote SourceUnreadable() => new(
        "source-unreadable",
        "This source could not be read, so nothing could be worked out from it. Check that the repository or archive is the right one and try again.",
        "این سورس خوانده نشد و چیزی از آن فهمیده نشد. ببین مخزن یا فایل فشرده درست است و دوباره امتحان کن.");

    public static InspectionNote EnvFileIgnored() => new(
        "env-file-ignored",
        "A .env file was found and deliberately not read: only example files are, because a real .env holds real secrets. Anything the app needs from it has to be entered here.",
        "یک فایل .env پیدا شد و عمداً خوانده نشد؛ فقط فایل‌های نمونه خوانده می‌شوند، چون .env واقعی رمزهای واقعی دارد. هر مقداری که اپ از آن لازم دارد باید اینجا وارد شود.");

    public static InspectionNote SeveralAppsFound() => new(
        "several-apps-found",
        "This source holds several apps. Choose the folder to launch; each of the others can be launched on its own.",
        "در این سورس چند اپ هست. پوشه‌ای را که می‌خواهی راه بیفتد انتخاب کن؛ هر کدام از بقیه را جداگانه می‌شود راه انداخت.");

    public static InspectionNote ComposeRunsOwnServices() => new(
        "compose-runs-own-services",
        "A compose file runs this app, and it starts its own databases and services, so nothing extra is proposed beside it.",
        "این اپ با فایل compose اجرا می‌شود و دیتابیس‌ها و سرویس‌هایش را خودش بالا می‌آورد، پس چیز دیگری کنارش پیشنهاد نشده.");

    public static InspectionNote NoStackRecognised() => new(
        "no-stack-recognised",
        "Nothing here says how to build this: no Dockerfile, and none of package.json, a .csproj, go.mod, composer.json, index.php, requirements.txt, pyproject.toml, Pipfile or index.html. Add a Dockerfile, or check this is the right source.",
        "چیزی اینجا نمی‌گوید این پروژه چطور ساخته می‌شود: نه Dockerfile هست و نه هیچ‌کدام از package.json، فایل csproj، go.mod، composer.json، index.php، requirements.txt، pyproject.toml، Pipfile یا index.html. یک Dockerfile اضافه کن، یا ببین سورس درست است.");

    /// <summary>The wording of <c>Buildpacks.DetectionHint</c>, which a parity test holds this to.</summary>
    public static InspectionNote DjangoUnsupported() => new(
        "django-unsupported",
        "found manage.py, which looks like Django — this buildpack does not auto-detect Django. Add a Dockerfile.",
        "فایل manage.py پیدا شد که یعنی این پروژه احتمالاً Django است؛ ساخت خودکار از Django پشتیبانی نمی‌کند. یک Dockerfile اضافه کن.");

    /// <summary>The wording of <c>Buildpacks.DetectionHint</c>, which a parity test holds this to.</summary>
    public static InspectionNote PythonEntryMissing(IEnumerable<string> entries)
    {
        var names = string.Join(", ", entries);
        return new InspectionNote(
            "no-stack-recognised",
            "looks like a Python project (requirements.txt/pyproject.toml/Pipfile) but none of " +
            $"its recognised entry files were found ({names}). Add a Dockerfile, or add one of those files.",
            $"این پروژه Python به نظر می‌رسد (requirements.txt/pyproject.toml/Pipfile) ولی هیچ‌کدام از فایل‌های شروعی که شناخته می‌شوند پیدا نشد ({names}). یک Dockerfile اضافه کن، یا یکی از این فایل‌ها را.");
    }

    public static InspectionNote StackAmbiguous() => new(
        "stack-ambiguous",
        "This package.json can start something, and another language's project file sits beside it. Without a Dockerfile this builds as Node. If Node is not what runs this app, add a Dockerfile.",
        "این package.json می‌تواند چیزی را اجرا کند و کنارش فایل پروژهٔ یک زبان دیگر هم هست. بدون Dockerfile این پروژه با Node ساخته می‌شود. اگر اپ با Node اجرا نمی‌شود، یک Dockerfile اضافه کن.");

    public static InspectionNote FrontendAssetsNotBuilt() => new(
        "frontend-assets-not-built",
        NodeStartRule.AssetsNotBuilt,
        "package.json اینجا اسکریپت build دارد ولی ایمیجی که خودکار ساخته می‌شود آن را اجرا نمی‌کند، پس فایل‌های فرانت‌اند ساخته نمی‌شوند. برای ساختنشان یک Dockerfile اضافه کن.");

    public static InspectionNote PublicVariablesReadAtBuild() => new(
        "public-variables-read-at-build",
        "Variables such as VITE_ are read when the site is built, not when it runs. What you enter here goes into the site's files, where any visitor can read it, and changing it later takes a new deploy.",
        "متغیرهایی مثل VITE_ هنگام ساخت خوانده می‌شوند، نه هنگام اجرا. مقداری که اینجا می‌دهی داخل فایل‌های سایت می‌رود و هر بازدیدکننده‌ای می‌تواند آن را ببیند، و برای عوض کردنش باید دوباره دیپلوی کنی.");

    public static InspectionNote SqliteNeedsVolume() => new(
        "sqlite-needs-volume",
        "This app uses SQLite, which keeps its data in a file. That file is lost on the next deploy unless a volume holds it.",
        "این اپ از SQLite استفاده می‌کند که داده‌هایش را در یک فایل نگه می‌دارد. اگر آن فایل روی یک volume نباشد، با دیپلوی بعدی از بین می‌رود.");

    public static InspectionNote SqlServerUnsupported() => new(
        "sql-server-unsupported",
        "This app uses SQL Server, which Harbora does not offer. It will need a SQL Server of your own to connect to, or a different database.",
        "این اپ از SQL Server استفاده می‌کند که Harbora ارائه نمی‌دهد. باید به یک SQL Server از خودت وصل شود، یا دیتابیس دیگری بگیرد.");

    public static InspectionNote DatabaseUnclear() => new(
        "database-unclear",
        "The example file names a database (DB_HOST and the like) but nothing says which engine it is. Choose the one this app uses, and fill those values in if they are still asked for.",
        "فایل نمونه اسم یک دیتابیس را آورده (مثل DB_HOST) ولی چیزی نمی‌گوید کدام نوع است. همانی را که این اپ استفاده می‌کند انتخاب کن، و اگر آن مقدارها هنوز پرسیده می‌شوند پرشان کن.");

    public static InspectionNote ManifestUnreadable(string file) => new(
        "manifest-unreadable",
        $"{file} could not be read (it is malformed, binary or over 1 MiB), so nothing was learned from it. What it would have said may be missing from this proposal.",
        $"فایل {file} خوانده نشد (خراب یا باینری است، یا بیشتر از ۱ مگابایت حجم دارد) و چیزی از آن فهمیده نشد. ممکن است چیزی که این فایل می‌گفت در این پیشنهاد نباشد.");

    public static InspectionNote TreeTooLarge() => new(
        "tree-too-large",
        "This source is too large to search all of it, so the search for .NET projects stopped early. If one was missed, set the root directory yourself.",
        "این سورس بزرگ‌تر از آن است که همه‌اش گشته شود و جستجوی پروژه‌های .NET زودتر متوقف شد. اگر پروژه‌ای پیدا نشده، پوشهٔ ریشه را خودت تعیین کن.");
}
