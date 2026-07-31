using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Output that a database can actually hold.
///
/// Found in production, in the worst possible shape: a release task ran, its output came back with
/// the NUL bytes from Docker's stream framing, and PostgreSQL rejected the whole write with
/// <c>invalid byte sequence for encoding "UTF8": 0x00</c>. Because the rejection happened inside
/// SaveChanges, the pipeline could not even record that it had failed — the deployment sat "in
/// progress" indefinitely, which is worse than a plain failure.
/// </summary>
public class LogTextTests
{
    [Fact]
    public void A_nul_byte_is_removed_because_postgres_cannot_store_one_at_all()
    {
        // Not escaped, not replaced — there is no encoding of NUL that a text column accepts.
        LogText.Clean("migrating\0done").Should().Be("migratingdone");
    }

    [Fact]
    public void Dockers_stream_framing_is_stripped()
    {
        // A container without a TTY has its output framed: one byte of stream id, three zero bytes,
        // then a four-byte length. All of it arrives looking like text.
        var framed = "\0\0\0\0\0\0MIGRATION_DONE";

        LogText.Clean(framed).Should().Be("MIGRATION_DONE");
    }

    [Fact]
    public void Ordinary_output_is_returned_unchanged()
    {
        const string text = "Applying migration '20260731_AddOrders'.\nDone (0.42s).";

        LogText.Clean(text).Should().BeSameAs(text, "the common case should not even copy the string");
    }

    [Fact]
    public void The_whitespace_that_shapes_output_survives()
    {
        // Stripping these would turn a readable stack trace or table into one long line.
        LogText.Clean("a\nb\tc\r\nd").Should().Be("a\nb\tc\r\nd");
    }

    [Fact]
    public void Text_before_the_first_bad_byte_is_kept()
    {
        // The interesting part of a log line is usually at the front of it.
        LogText.Clean("connected to db\0\0 then failed").Should().Be("connected to db then failed");
    }

    [Fact]
    public void Non_ascii_output_is_left_alone()
    {
        // Log lines are not all English, and a sanitiser that eats them is worse than the bug.
        LogText.Clean("مهاجرت انجام شد ✅").Should().Be("مهاجرت انجام شد ✅");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_gives_an_empty_string_out(string? input)
    {
        LogText.Clean(input).Should().Be("");
    }
}
