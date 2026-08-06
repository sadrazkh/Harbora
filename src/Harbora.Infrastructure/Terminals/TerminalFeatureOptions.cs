namespace Harbora.Infrastructure.Terminals;

/// <summary>
/// Whether this installation offers a terminal at all.
///
/// Bound to the same <c>Features</c> section as the other module switches, and <b>off unless an
/// operator turns it on</b>. This is not the usual caution about new code: a shell in a customer's
/// container is the one capability where the platform's own guards stop applying, and an operator
/// who has not decided to offer it should not be offering it because it shipped.
/// </summary>
public sealed class TerminalFeatureOptions
{
    public const string SectionName = "Features";

    public bool Terminal { get; set; }
}
