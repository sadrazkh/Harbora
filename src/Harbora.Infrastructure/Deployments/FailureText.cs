using System.Text;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Turns a thrown exception into the sentence a person should read.
///
/// <para>
/// <c>ex.Message</c> alone is not that sentence. .NET's own wrapper messages say nothing and then
/// point at what they are hiding: an <c>HttpRequestException</c> around a broken socket reports
/// <c>"The requested failed, see inner exception for details."</c> — and the pipeline stored exactly
/// that, dropping the chain it names. Seven consecutive live deployments failed with that one
/// useless line in the database, in the API and in the CLI, while the real cause
/// (<c>Broken pipe</c>, from a Docker client that could not stream a large build context) existed
/// only in the panel container's stdout. Diagnosing it took hours instead of the two minutes the
/// inner message would have cost.
/// </para>
///
/// <para>
/// So: walk the chain, keep what each level adds, and put the innermost cause last — the deployment
/// log's final line is <c>❌ Deployment failed: …</c>, and the last thing written is the thing that
/// gets read.
/// </para>
/// </summary>
public static class FailureText
{
    /// <summary>
    /// Long enough for a real chain with a command's output in it, short enough that a pathological
    /// one cannot fill a page nobody will read to the end of.
    /// </summary>
    private const int MaxLength = 2000;

    private const string Separator = " → ";

    /// <summary>
    /// Every distinct thing the chain has to say, outermost first, joined by <c>→</c>.
    ///
    /// <para>
    /// A level is dropped when it adds nothing: an empty message, a repeat of one already kept, or
    /// one wholly contained in the level above it. That last rule is what collapses a
    /// <c>SocketException</c>'s bare <c>"Broken pipe"</c> into the <c>IOException</c>'s
    /// <c>"Unable to write data to the transport connection: Broken pipe"</c> that already said it
    /// with context, rather than printing the same two words twice.
    /// </para>
    /// </summary>
    public static string Describe(Exception? ex)
    {
        if (ex is null) return "";

        var kept = new List<string>();
        foreach (var message in Chain(ex))
        {
            var line = message.Trim();
            if (line.Length == 0) continue;
            // Contained in something already kept, in either direction: the shorter one is the one
            // that loses, because the longer said the same thing and said where it happened.
            if (kept.Any(k => k.Contains(line, StringComparison.Ordinal))) continue;
            kept.RemoveAll(k => line.Contains(k, StringComparison.Ordinal));
            kept.Add(line);
        }

        if (kept.Count == 0) return ex.GetType().Name;

        var joined = string.Join(Separator, kept);
        return joined.Length <= MaxLength ? joined : joined[..MaxLength].TrimEnd() + "…";
    }

    /// <summary>
    /// The messages of an exception and everything under it, outermost first.
    ///
    /// <para>
    /// <see cref="AggregateException"/> is walked across rather than down: its own message is a
    /// count ("One or more errors occurred"), and the thing that actually failed is one of its
    /// inner exceptions, not its <see cref="Exception.InnerException"/> alone. A parallel step that
    /// fails is otherwise reported as a tally of itself.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Chain(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                foreach (var message in Chain(inner))
                    yield return message;
            yield break;
        }

        yield return ex.Message;

        if (ex.InnerException is { } next)
            foreach (var message in Chain(next))
                yield return message;
    }
}
