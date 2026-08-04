namespace Harbora.NodeAgent.Commands;

/// <summary>
/// The verbs this build actually has a handler for.
///
/// <para>
/// A one-field holder rather than a direct dependency because the honest source of the answer — the
/// dispatcher — sits downstream of the handlers, which sit downstream of the deployer, which
/// publishes events over the channel, which reports capabilities. Reading it through this breaks
/// that loop without letting the capability report drift into a hand-maintained list.
/// </para>
/// </summary>
public sealed class ImplementedCommands
{
    private IReadOnlyList<string> _names = [];

    public IReadOnlyList<string> Names => _names;

    internal void Set(IEnumerable<string> names) =>
        _names = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
}
