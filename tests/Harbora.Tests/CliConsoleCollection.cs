using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every test class that captures CLI output belongs to this collection, so no two of them run at
/// the same time.
///
/// <para>
/// <c>AnsiConsole.Console</c> is a <b>process-global static</b>. The capture helper each of those
/// classes uses — <c>var previous = AnsiConsole.Console; AnsiConsole.Console = …; try { … } finally
/// { AnsiConsole.Console = previous; }</c> — is correct on its own and wrong three times over: xUnit
/// runs separate collections in parallel, so two of these swaps interleave and one test's output is
/// written into the other's <see cref="System.IO.StringWriter"/>. The test that loses then asserts
/// against somebody else's text, or against nothing at all.
/// </para>
///
/// <para>
/// It surfaced as <c>CliEnvPullTests.An_existing_file_that_already_matches_is_reported_as_such_and_left_alone</c>
/// failing in the full suite while passing 3/3 on its own — the shape of a shared-state race, not of
/// a broken assertion. It is also the reason to fix it here rather than to retry it: a test that
/// reads another test's output can pass for the wrong reason just as easily as it can fail, and
/// nothing would report that.
/// </para>
///
/// <para>
/// A collection rather than a lock inside the helper: the helper is duplicated in each class (each
/// one's comment says it mirrors the others), so a lock would have to be duplicated correctly three
/// times and again in the fourth class somebody adds. Collection membership is one attribute the
/// next such class either has or visibly does not.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class CliConsoleCollection
{
    public const string Name = "cli-console";
}
