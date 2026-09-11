
using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The stored reason a deployment failed.
///
/// <para>
/// Deployments 31-37 on the live server all failed with one sentence in the database, the API and
/// the CLI: <c>"The requested failed, see inner exception for details."</c> — .NET's own wrapper
/// message, stored by a pipeline that then dropped the details it names. The actual cause reached
/// the panel container's stdout and nowhere else.
/// </para>
/// </summary>
public class FailureTextTests
{
    /// <summary>
    /// The exact shape of the live failure, rebuilt.
    ///
    /// <para>
    /// The innermost level is a plain exception carrying the message Linux produces, NOT a real
    /// <c>SocketException(32)</c>. Error 32 is <c>EPIPE</c> — "Broken pipe" — only on Unix; on
    /// Windows the same constructor yields "The process cannot access the file because it is being
    /// used by another process", because the number is read against a different error table. A test
    /// built on it would assert one thing in CI and a different thing on the machine it was written
    /// on. The server this failure came from runs Linux; the message is what matters here, so the
    /// message is what the fixture states.
    /// </para>
    /// </summary>
    private static Exception TheLiveFailure() =>
        new HttpRequestException("The requested failed, see inner exception for details.",
            new IOException("Unable to write data to the transport connection: Broken pipe",
                new IOException("Broken pipe")));

    [Fact]
    public void The_reason_that_was_lost_is_the_one_that_survives()
    {
        var described = FailureText.Describe(TheLiveFailure());

        described.Should().Contain("Broken pipe",
            "this is the whole point — it is what the seven failed deployments never recorded");
    }

    [Fact]
    public void The_innermost_cause_comes_last_because_the_last_line_is_what_gets_read()
    {
        // The deployment log's final line is "❌ Deployment failed: {reason}".
        var described = FailureText.Describe(TheLiveFailure());

        described.Should().EndWith("Broken pipe");
        described.IndexOf("see inner exception", StringComparison.Ordinal)
            .Should().BeLessThan(described.IndexOf("Broken pipe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_level_that_only_repeats_the_one_above_it_is_dropped()
    {
        // The innermost level's message is the bare "Broken pipe", which the level above it already
        // said with the context of where it happened. Printing it twice is noise.
        var described = FailureText.Describe(TheLiveFailure());

        described.Split("Broken pipe").Length.Should().Be(2, "'Broken pipe' should appear exactly once");
    }

    [Fact]
    public void A_plain_exception_is_returned_unchanged()
    {
        FailureText.Describe(new InvalidOperationException("No Dockerfile found."))
            .Should().Be("No Dockerfile found.");
    }

    [Fact]
    public void An_aggregate_reports_what_failed_rather_than_how_many_things_did()
    {
        // AggregateException's own message is a count. The thing that broke is inside it.
        var described = FailureText.Describe(
            new AggregateException(new InvalidOperationException("port 8080 already allocated")));

        described.Should().Contain("port 8080 already allocated");
        described.Should().NotContain("One or more errors occurred");
    }

    [Fact]
    public void A_null_exception_describes_as_empty_rather_than_throwing()
    {
        // This runs on a failure path. A helper that throws there costs the deployment its record
        // of having failed at all.
        FailureText.Describe(null).Should().BeEmpty();
    }

    [Fact]
    public void An_exception_with_no_message_still_names_itself()
    {
        // Better than storing "" and leaving the page blank next to a Failed badge.
        FailureText.Describe(new BlankException()).Should().Be(nameof(BlankException));
    }

    [Fact]
    public void A_pathological_chain_is_bounded()
    {
        Exception ex = new InvalidOperationException(new string('x', 400));
        for (var i = 0; i < 40; i++)
            ex = new InvalidOperationException(new string((char)('a' + i % 26), 400), ex);

        FailureText.Describe(ex).Length.Should().BeLessThanOrEqualTo(2001,
            "a wall of text nobody reads to the end of is not an improvement on one useless line");
    }

    private sealed class BlankException : Exception
    {
        public override string Message => "";
    }
}
