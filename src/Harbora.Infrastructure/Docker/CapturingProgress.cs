using System.Text;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Collects a one-off container's output instead of streaming it live, so it can be handed back
/// as a single JSON field over HTTP — see the <c>/agent/oneoff</c> endpoint and the
/// <see cref="RemoteDockerEngine.RunOneOffAsync"/> that reads its response.
///
/// Bounded, because the caller decides what runs and a helper that prints a great deal — a tar
/// listing of a large volume — must not be able to grow this without limit in the agent's memory
/// or on the wire. Once the bound is reached, further lines are dropped and one marker line says
/// so: a listing that silently stops partway through is the same "looks empty, isn't" defect this
/// codebase keeps finding, wearing different clothes.
/// </summary>
public sealed class CapturingProgress(int maxChars) : IProgress<string>
{
    /// <summary>
    /// Roughly 1 MiB of text. Generous for even a large directory listing — tens of thousands of
    /// <c>type|size|mtime|name</c> lines, the format <c>VolumeFileCommands</c> prints — while still
    /// keeping one run's output a bounded, predictable size to hold in the agent's memory, carry
    /// over HTTP, and hold again in the panel.
    /// </summary>
    public const int DefaultMaxChars = 1024 * 1024;

    /// <summary>
    /// The start of the marker line <see cref="Report"/> appends once the bound is hit. Exposed so a
    /// parser reading this output back — <c>VolumeFileCommands.ParseListing</c> today — can recognise
    /// the line as "the listing stopped here" instead of treating it as just another row it cannot
    /// make sense of and silently dropping it, which is the same "looks complete, isn't" defect this
    /// class exists to close, one layer up.
    /// </summary>
    public const string TruncationMarkerPrefix = "... [output truncated:";

    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();
    private bool _truncated;

    public void Report(string value)
    {
        lock (_gate)
        {
            if (_truncated) return;

            if (_text.Length + value.Length + 1 > maxChars)
            {
                _text.Append(TruncationMarkerPrefix).Append(" exceeded ").Append(maxChars).Append(" characters]\n");
                _truncated = true;
                return;
            }

            _text.Append(value).Append('\n');
        }
    }

    /// <summary>Everything captured so far, one line per <see cref="Report"/> call, newline-joined.</summary>
    public string Text
    {
        get { lock (_gate) return _text.ToString(); }
    }
}
