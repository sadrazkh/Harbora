using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Launch;
using Harbora.Shared.Inspection;

namespace Harbora.Infrastructure.Launch;

/// <summary>
/// The answer to "where is your code?", made safe to keep: a URL with no credentials in it, and a
/// name to call the app. Pure, so every spelling a person might paste can be tried without a network.
/// </summary>
public static partial class LaunchSource
{
    /// <param name="CloneUrl">The URL to clone, with any credentials, query and fragment removed. Null when refused.</param>
    /// <param name="EmbeddedToken">What was in the URL's user-info. Moved out, because a URL gets printed and a token must not.</param>
    public sealed record CloneUrlReading(string? CloneUrl, string? EmbeddedToken, ServiceRefusal? Refusal);

    private const int MaxUrlLength = 2048;
    private const string UrlField = nameof(LaunchStartRequest.CloneUrl);

    public static CloneUrlReading ReadCloneUrl(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
            return Refused("A repository URL is required.", "آدرس مخزن لازم است.");

        if (text.Length > MaxUrlLength)
            return Refused("That URL is too long to be a repository address.", "این آدرس برای یک مخزن بیش از حد بلند است.");

        // git@github.com:acme/app.git: the form a repository's own page hands out first. Say what to
        // type instead rather than only that this is wrong, because the https form always exists.
        if (ScpStyle().Match(text) is { Success: true } scp)
            return SshRefused($"https://{scp.Groups["host"].Value}/{scp.Groups["path"].Value}");

        if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var ssh) && ssh.Host.Length > 0)
            return SshRefused($"https://{ssh.Host}{ssh.AbsolutePath}");

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0)
            return Refused(
                $"Only http and https repository URLs can be read. Try https://{text.TrimStart('/')}",
                $"فقط آدرس‌های http و https خوانده می‌شوند. این را امتحان کن: https://{text.TrimStart('/')}");

        var token = TokenIn(uri.UserInfo);
        var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return new CloneUrlReading(clean, token, null);
    }

    /// <summary>
    /// <c>user:token</c> is a token in the password place; a lone <c>token</c> is one in the user place,
    /// which is what GitHub and GitLab both accept and what <c>LibGit2GitService</c> sends.
    /// </summary>
    private static string? TokenIn(string userInfo)
    {
        if (userInfo.Length == 0) return null;
        var colon = userInfo.IndexOf(':');
        var secret = colon >= 0 && colon < userInfo.Length - 1 ? userInfo[(colon + 1)..] : userInfo.TrimEnd(':');
        return secret.Length == 0 ? null : Uri.UnescapeDataString(secret);
    }

    private static CloneUrlReading SshRefused(string https) => Refused(
        $"An SSH address cannot be read here; use the https form instead: {https}",
        $"آدرس SSH اینجا خوانده نمی‌شود؛ شکل https آن را بده: {https}");

    private static CloneUrlReading Refused(string en, string fa) => new(null, null, new ServiceRefusal(en, fa, UrlField));

    /// <summary>The repository's own name: what a person calls it, and what the app starts out as.</summary>
    public static string NameFromCloneUrl(string cloneUrl)
    {
        var path = Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : cloneUrl;
        var last = path.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        return last.Length == 0 ? FallbackName : last;
    }

    /// <summary>
    /// The archive's name without its extension or the unique part a caller put in it so two uploads
    /// could not collide (<c>my-shop-0190…ef.zip</c>). A name that says nothing about the project,
    /// because the caller had none to keep, becomes a plain <c>app</c> the person can change.
    /// </summary>
    public static string NameFromArchive(string archivePath)
    {
        var name = LabelForArchive(archivePath);
        foreach (var extension in (string[])[".tar.gz", ".tgz", ".zip"])
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                name = name[..^extension.Length];

        return name.Length == 0 || name.ToLowerInvariant() is "launch" or "upload" or "source" or "archive"
            ? FallbackName
            : name;
    }

    /// <summary>What to call the archive on a page: its file name, without the unique part.</summary>
    public static string LabelForArchive(string archivePath) =>
        UniquePart().Replace(Path.GetFileName(archivePath.Replace('\\', '/')), "").Trim('-', '_', ' ');

    private const string FallbackName = "app";

    [GeneratedRegex(@"^[\w.\-]+@(?<host>[\w.\-]+):(?<path>[^:\s]+)$")]
    private static partial Regex ScpStyle();

    /// <summary>A GUID, dashed or not, at any point in the name.</summary>
    [GeneratedRegex(@"[-_]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex UniquePart();
}

/// <summary>
/// What <see cref="AppLaunch.InspectionJson"/> holds. A repository with several apps in it needs each of
/// them read while the tree exists, because the tree is deleted straight after and the person chooses
/// later: a folder only read at that point could not be read at all, and the launch would go ahead with
/// no database, no variables and the wrong port.
/// </summary>
/// <param name="Root">What the tree as a whole was found to be. Its <c>RootCandidates</c> are the keys of <paramref name="Candidates"/>.</param>
/// <param name="Candidates">Each folder a person can choose, with its <c>RootDirectory</c> relative to the tree rather than to that folder.</param>
/// <param name="Chosen">The folder chosen so far, a key of <paramref name="Candidates"/>.</param>
/// <param name="GitProviderId">
/// The connected account the repository is read through: the one it was picked from, or the one whose
/// own list held a pasted address. Kept here because this column is the launch's account of its source,
/// and it is written before the inspection (a pick) as well as by it. The app is created under this
/// account rather than under a provider of its own, and its token never touches the launch row.
/// </param>
public sealed record StoredInspection(
    SourceInspection Root,
    IReadOnlyDictionary<string, SourceInspection> Candidates,
    string? Chosen = null,
    Guid? GitProviderId = null)
{
    /// <summary>What a launch picked from a connected account holds before it has been read.</summary>
    public static StoredInspection ReadThrough(Guid gitProviderId) =>
        new(SourceInspection.Nothing, new Dictionary<string, SourceInspection>(), GitProviderId: gitProviderId);

    /// <summary>
    /// The chosen folder's inspection, or the root's while nothing is chosen. The folders stay listed on
    /// it so the person can change their mind, right up to the confirm.
    /// </summary>
    public SourceInspection Current => Chosen is { } chosen && Candidates.TryGetValue(chosen, out var candidate)
        ? candidate with { RootCandidates = Root.RootCandidates }
        : Root;
}

/// <summary>
/// What a person typed on the confirm screen, kept on the launch for as long as a failed step might
/// still need it and no longer. A launch row is no place for somebody's API key in the open, so every
/// answer goes through <c>ISecretProtector</c> as one value; only the names are readable, because they
/// are what a sentence has to say when the answers are dropped before anything used them.
/// </summary>
/// <param name="Names">The variables a value was typed for. Never a value.</param>
/// <param name="Sealed">Every answer, blank ones included, as protected JSON: a retry is handed exactly what the confirm was.</param>
public sealed record SealedAnswers(IReadOnlyList<string> Names, string Sealed)
{
    /// <summary>A record prints every property by default, and this one is ciphertext of somebody's keys.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Names = [{string.Join(", ", Names)}]");
        return true;
    }
}

/// <summary>
/// How a launch's JSON columns are written: enums as names, so a stored row still reads the same after
/// an enum grows. Reads are tolerant, because a row that cannot be parsed must not take a page down.
/// </summary>
public static class LaunchJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static StoredInspection ReadStored(string? json)
    {
        var stored = Read<StoredInspection>(json);
        return stored?.Root is null
            ? new StoredInspection(SourceInspection.Nothing, new Dictionary<string, SourceInspection>(), GitProviderId: stored?.GitProviderId)
            : stored with { Candidates = stored.Candidates ?? new Dictionary<string, SourceInspection>() };
    }

    /// <summary>What the launch is being judged from right now. See <see cref="StoredInspection.Current"/>.</summary>
    public static SourceInspection ReadInspection(string? json) => ReadStored(json).Current;

    public static LaunchChoices? ReadChoices(string? json) => Read<LaunchChoices>(json);

    /// <summary>
    /// What is kept of the confirm screen: every choice, and no variable's value in the open. A value
    /// lives on the app's own environment rows once it is set, encrypted if it is a secret. Until then,
    /// which can be a failed step and a retry later, what was typed rides along as one sealed value
    /// (<paramref name="answers"/>), taken off again by <see cref="WithoutAnswers"/> the moment the
    /// variables are on the app.
    /// </summary>
    public static string WriteChoices(LaunchChoices choices, SealedAnswers? answers = null)
    {
        var node = JsonSerializer.SerializeToNode(choices with
        {
            EnvVars = choices.EnvVars.Select(v => v with { Value = null }).ToList()
        }, Options)!.AsObject();
        if (answers is not null) node[AnswersProperty] = JsonSerializer.SerializeToNode(answers, Options);
        return node.ToJsonString(Options);
    }

    /// <summary>
    /// The property <see cref="WriteChoices"/> puts the sealed answers under. A reader of
    /// <see cref="LaunchChoices"/> skips it, and the tick looks for it by name to find a failed launch
    /// still holding some.
    /// </summary>
    public const string AnswersProperty = "sealedAnswers";

    /// <summary>The marker a query can look for in the column without parsing it.</summary>
    public const string AnswersMarker = "\"" + AnswersProperty + "\"";

    public static SealedAnswers? ReadAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains(AnswersMarker, StringComparison.Ordinal)) return null;
        try
        {
            var answers = JsonNode.Parse(json)?[AnswersProperty]?.Deserialize<SealedAnswers>(Options);
            return answers is { Sealed.Length: > 0 } ? answers with { Names = answers.Names ?? [] } : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The same choices with the sealed answers taken off, or the column as it was when it holds none.</summary>
    public static string? WithoutAnswers(string? json)
    {
        if (ReadAnswers(json) is null) return json;
        var node = JsonNode.Parse(json!)!.AsObject();
        node.Remove(AnswersProperty);
        return node.ToJsonString(Options);
    }

    public static List<Guid> ReadIds(string? json) => Read<List<Guid>>(json) ?? [];

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Where the inspection is read from. The seam exists so a launch's own tests can say what a source
/// tree was found to need without building a tree for every answer; the inspector has tests of its own.
/// </summary>
public interface ILaunchSourceReader
{
    SourceInspection Read(string root);
}

/// <inheritdoc />
public sealed class SourceInspectorReader : ILaunchSourceReader
{
    public SourceInspection Read(string root) => SourceInspector.Inspect(root);
}
