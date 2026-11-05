using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Verification;

/// <summary>Something recognisable in an app's log.</summary>
/// <param name="Code">Stable: what a test or a future screen can key on.</param>
/// <param name="Message">What it means and what to do, in one or two plain sentences.</param>
/// <param name="MessageFa">The same in Persian.</param>
/// <param name="Fragment">
/// The matched words and nothing more of the line. Still not trusted: it can hold a table or module
/// name, so the caller redacts it before anybody sees it.
/// </param>
public sealed record AppLogFinding(string Code, string Message, string MessageFa, string Fragment);

/// <summary>
/// Reads an app's own log for the handful of failures that mean the same thing in every language and
/// framework: it is talking to itself, it cannot reach something, its password is wrong, its tables
/// do not exist.
///
/// <para>
/// Pure, so each pattern is one statement tested on a real-looking line rather than something only
/// seen against a live container. Deliberately narrow: this feeds a <c>Warning</c> on a page somebody
/// reads to decide whether their app is healthy, and a reader that fires on a log that merely says
/// "connection" teaches them to ignore it.
/// </para>
///
/// <para>
/// Every expression is <see cref="RegexOptions.NonBacktracking"/>, because the input is whatever a
/// customer's program chose to print and these run on a request.
/// </para>
/// </summary>
public static class AppLogDiagnosis
{
    public const int MaxFindings = 3;

    public const string ConnectingToItself = "connecting-to-itself";
    public const string CannotReach = "cannot-reach";
    public const string WrongCredentials = "wrong-credentials";
    public const string MigrationsNotRun = "migrations-not-run";
    public const string MissingDependency = "missing-dependency";
    public const string PortInUse = "port-in-use";
    public const string ListeningOnLoopback = "listening-on-loopback";

    private const int MaxFragmentChars = 100;

    /// <summary>A single line longer than this is a dumped payload, not a sentence with a diagnosis in it.</summary>
    private const int MaxLineChars = 2000;

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>The colour codes Node and .NET loggers print, which would split a phrase in two.</summary>
    private static readonly Regex AnsiColour = new(@"\x1B\[[0-9;?]*[A-Za-z]", Options);

    // ---- connecting to itself ----

    /// <summary>
    /// Loopback and a database's port, in the three spellings clients use: <c>127.0.0.1:5432</c>,
    /// <c>[::1]:5432</c>, and libpq's <c>"localhost" (127.0.0.1), port 5432</c>. Not preceded by a word
    /// character, so <c>my-localhost:5432</c> and <c>10.127.0.0.1</c> are not it, and the port ends there,
    /// so <c>:54321</c> is not it either.
    /// </summary>
    private static readonly Regex LoopbackDatabasePort = new(
        @"(?:^|[^\w.:\-])(?<hit>(?:localhost|127\.0\.0\.1|\[?::1\]?)[""')\s]*(?:\([^)]{0,40}\)[""'\s]*)?[,:\s]*(?:port\s*[=:]?\s*)?(?:5432|3306|27017|6379|5672))\b",
        Options);

    /// <summary>The older libpq wording, whose second line names the host and the port with the error on the line before.</summary>
    private static readonly Regex LibpqAskingAboutLoopback = new(
        @"(?<hit>on host ""(?:localhost|127\.0\.0\.1|::1)"".{0,80}on port (?:5432|3306))\b", Options);

    private static readonly Regex MySqlOnLoopback = new(
        @"(?<hit>can't connect to (?:local )?mysql server (?:on '(?:localhost|127\.0\.0\.1)|through socket))", Options);

    /// <summary>Required on the same line as the address, or a line saying it connected would count.</summary>
    private static readonly Regex ConnectError = new(
        @"(?-i:ECONNREFUSED|ETIMEDOUT|EHOSTUNREACH|UnableToConnect)|connection refused|actively refused|" +
        @"connect failed|connection failed|connection error|connection timed out|failed to connect|" +
        @"could not connect|can't connect|cannot connect|unable to connect|dial tcp|error 111|errno 111",
        Options);

    // ---- the rest of the table: each one's words, as they really appear ----

    private static readonly Regex CannotReachWords = new(
        @"(?-i:\bECONNREFUSED\b)|connection refused|could not connect to server|getaddrinfo ENOTFOUND|" +
        @"getaddrinfo EAI_AGAIN|name or service not known|could not translate host name|no such host|" +
        @"temporary failure in name resolution",
        Options);

    private static readonly Regex WrongCredentialWords = new(
        @"password authentication failed|access denied for user|authentication failed|" +
        @"(?-i:\bWRONGPASS\b|\bNOAUTH\b|\bACCESS_REFUSED\b)",
        Options);

    private static readonly Regex MissingTableWords = new(
        @"relation ""[^""]{1,80}"" does not exist|table '[^']{1,80}' doesn't exist|no such table(?:: ?[\w.]{1,80})?|" +
        @"the table `[^`]{1,80}` does not exist",
        Options);

    private static readonly Regex MissingDependencyWords = new(
        @"cannot find module(?: '[^']{1,80}')?|modulenotfounderror(?:: no module named '[^']{1,80}')?|" +
        @"importerror: no module named \S{1,80}|could not load file or assembly(?: '[^',]{1,80})?",
        Options);

    private static readonly Regex PortInUseWords = new(
        @"(?-i:\bEADDRINUSE\b)|address already in use", Options);

    /// <summary>
    /// Only what a server prints about where it really bound. Not <c>listening on http://localhost</c>
    /// in general: that is the sentence a Node app's own <c>console.log</c> prints whatever it bound to,
    /// and it would flag half the healthy Express apps there are. Kestrel's "Now listening on:" is the
    /// framework reporting a fact, so it is included.
    /// </summary>
    private static readonly Regex LoopbackBindWords = new(
        @"(?:running on|listening on:?|listening at:?)\s+(?:(?:https?|tcp)://)?127\.0\.0\.1|" +
        @"now listening on:\s+https?://localhost|bind(?:ing)?(?: to)? 127\.0\.0\.1",
        Options);

    /// <summary>
    /// Flask bound to all addresses prints its loopback address too, on the next line, so one
    /// "Running on http://127.0.0.1" proves nothing while a line like this is also there.
    /// </summary>
    private static readonly Regex AllInterfacesWords = new(
        @"(?:running on|listening on:?|listening at:?|now listening on:|bound to|bind(?:ing)?(?: to)?)\s+" +
        @"(?:all addresses|(?:(?:https?|tcp)://)?(?:0\.0\.0\.0|\[::\]|\*:|\+:))",
        Options);

    // ---- masking: for anything longer than a fragment ----

    private static readonly Regex UrlPassword = new(@"(?<head>://[^/\s:@]{0,128}:)[^@\s/]{1,256}@", Options);

    private static readonly Regex NamedSecret = new(
        @"(?<key>[\w.\-]{0,64}(?:password|passwd|pwd|secret|token|api[_\-]?key)[""']?\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s,;&]+)",
        Options);

    private static readonly Regex BearerToken = new(@"(?<key>bearer\s+)\S+", Options);

    private sealed record Rule(string Code, string Message, string MessageFa, Func<string, string?> Match);

    /// <summary>
    /// In the order they are reported, which is the order worth reading them in: when a log has more
    /// than <see cref="MaxFindings"/> of these, the ones about the app's wiring are the ones kept.
    /// </summary>
    private static readonly Rule[] Rules =
    [
        new(ConnectingToItself,
            "The app is looking for its database inside its own container, so it is ignoring the address it was given. " +
            "Make it read DATABASE_URL, or the name shown on the app's page.",
            "اپ دیتابیس را داخل کانتینر خودش می‌جوید، یعنی آدرسی را که به آن داده شده نمی‌خواند. " +
            "کاری کن DATABASE_URL یا نامی را که در صفحهٔ اپ نوشته شده بخواند.",
            MatchSelfConnection),

        new(CannotReach,
            "The app cannot reach something it needs: the address is wrong, or whatever is there is not answering. " +
            "Check the addresses in its variables, and that the service is running.",
            "اپ به چیزی که لازم دارد نمی‌رسد: یا آدرس غلط است یا آنجا کسی جواب نمی‌دهد. " +
            "آدرس‌های داخل متغیرها و روشن بودن آن سرویس را بررسی کن.",
            line => First(CannotReachWords, line)),

        new(WrongCredentials,
            "The app reached the database with the wrong username or password. " +
            "Often an old value in the app's own variables is outranking the attachment: remove it.",
            "اپ به دیتابیس رسیده ولی نام کاربری یا رمزش غلط است. " +
            "معمولاً یک مقدار قدیمی در متغیرهای خود اپ جای مقدار اتصال را گرفته؛ آن را پاک کن.",
            line => First(WrongCredentialWords, line)),

        new(MigrationsNotRun,
            "The database is empty: the app's tables do not exist, so its migrations have not run. " +
            "Set a release command that runs them.",
            "دیتابیس خالی است: جدول‌های اپ وجود ندارند، پس مایگریشن‌هایش اجرا نشده. " +
            "یک «دستور پیش از انتشار» بگذار که آن‌ها را اجرا کند.",
            line => First(MissingTableWords, line)),

        new(MissingDependency,
            "A dependency is missing from the build: the app is asking for code that is not in its image. " +
            "Add it to the project's dependency file and deploy again.",
            "یک وابستگی در بیلد نیست: اپ کدی را می‌خواهد که داخل ایمیجش نیست. " +
            "آن را به فایل وابستگی‌های پروژه اضافه کن و دوباره مستقر کن.",
            line => First(MissingDependencyWords, line)),

        new(PortInUse,
            "Two things inside the container want the same port. Make sure only one of them listens on it.",
            "دو چیز داخل کانتینر یک پورت را می‌خواهند. کاری کن فقط یکی روی آن گوش بدهد.",
            line => First(PortInUseWords, line)),

        new(ListeningOnLoopback,
            "It listens only inside its own container, so nothing can reach it. Make it listen on 0.0.0.0.",
            "فقط داخل کانتینر خودش گوش می‌دهد، پس هیچ‌چیز به آن نمی‌رسد. کاری کن روی 0.0.0.0 گوش بدهد.",
            line => First(LoopbackBindWords, line))
    ];

    /// <summary>At most <see cref="MaxFindings"/> findings, one per kind. Nothing recognisable is an empty list.</summary>
    public static IReadOnlyList<AppLogFinding> Find(IEnumerable<string> lines)
    {
        var found = new Dictionary<string, AppLogFinding>(StringComparer.Ordinal);
        var boundEverywhere = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = AnsiColour.Replace(raw.Length > MaxLineChars ? raw[..MaxLineChars] : raw, "");
            boundEverywhere |= AllInterfacesWords.IsMatch(line);

            // The first rule that recognises a line takes it: "connect ECONNREFUSED 127.0.0.1:5432" is
            // one problem with a precise name, not that and also a vaguer one.
            foreach (var rule in Rules)
            {
                if (rule.Match(line) is not { } fragment) continue;

                found.TryAdd(rule.Code, new AppLogFinding(rule.Code, rule.Message, rule.MessageFa, Shorten(fragment)));
                break;
            }
        }

        // libpq's older wording spreads one failure over two lines, and only the second names the host.
        if (found.ContainsKey(ConnectingToItself)) found.Remove(CannotReach);
        if (boundEverywhere) found.Remove(ListeningOnLoopback);

        return Rules
            .Where(rule => found.ContainsKey(rule.Code))
            .Select(rule => found[rule.Code])
            .Take(MaxFindings)
            .ToList();
    }

    /// <summary>
    /// Hides a password written into a URL or beside its own name. For quoting more than a fragment of
    /// a log: the redactor only knows values this platform holds, and a password hard-coded in the
    /// customer's program, or left over from last month, is not one of them.
    /// </summary>
    public static string MaskCredentials(string text)
    {
        var masked = UrlPassword.Replace(text, "${head}***@");
        masked = NamedSecret.Replace(masked, "${key}***");
        return BearerToken.Replace(masked, "${key}***");
    }

    private static string? MatchSelfConnection(string line)
    {
        var libpq = LibpqAskingAboutLoopback.Match(line);
        if (libpq.Success) return libpq.Groups["hit"].Value;

        var mySql = MySqlOnLoopback.Match(line);
        if (mySql.Success) return mySql.Groups["hit"].Value;

        var address = LoopbackDatabasePort.Match(line);
        return address.Success && ConnectError.IsMatch(line) ? address.Groups["hit"].Value : null;
    }

    private static string? First(Regex words, string line)
    {
        var match = words.Match(line);
        return match.Success ? match.Value : null;
    }

    private static string Shorten(string fragment)
    {
        var trimmed = fragment.Trim();
        return trimmed.Length <= MaxFragmentChars ? trimmed : trimmed[..MaxFragmentChars];
    }
}
