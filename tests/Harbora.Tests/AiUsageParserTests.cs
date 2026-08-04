using FluentAssertions;
using Harbora.Infrastructure.Ai;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Reading the token counts a provider reported.
///
/// These numbers become a bill. Counts are never estimated locally — a number we invent is one we
/// cannot defend when a customer queries their invoice, and every provider tokenises differently.
/// </summary>
public class AiUsageParserTests
{
    [Fact]
    public void The_openai_field_names_are_read()
    {
        var (input, output, _) = AiUsageParser.Read(
            """{"usage":{"prompt_tokens":120,"completion_tokens":45}}""");

        input.Should().Be(120);
        output.Should().Be(45);
    }

    [Fact]
    public void The_responses_api_field_names_are_read_too()
    {
        // Same provider, different endpoint, different names for the same thing.
        var (input, output, _) = AiUsageParser.Read(
            """{"usage":{"input_tokens":300,"output_tokens":70}}""");

        input.Should().Be(300);
        output.Should().Be(70);
    }

    [Fact]
    public void Cached_input_is_read_from_its_nested_place()
    {
        var (_, _, cached) = AiUsageParser.Read(
            """{"usage":{"prompt_tokens":1000,"prompt_tokens_details":{"cached_tokens":800}}}""");

        cached.Should().Be(800);
    }

    [Fact]
    public void A_response_without_usage_reports_nothing_rather_than_guessing()
    {
        AiUsageParser.Read("""{"choices":[]}""").Should().Be((0L, 0L, 0L));
    }

    [Fact]
    public void An_unparseable_body_is_charged_as_zero()
    {
        // Under-billing a malformed response is recoverable. Inventing a number is not.
        AiUsageParser.Read("not json at all").Should().Be((0L, 0L, 0L));
        AiUsageParser.Read("").Should().Be((0L, 0L, 0L));
        AiUsageParser.Read(null).Should().Be((0L, 0L, 0L));
    }

    [Fact]
    public void Usage_of_the_wrong_shape_is_ignored_rather_than_crashing()
    {
        // A provider returning a string where a number belongs must not take the gateway down
        // after the customer already has their answer.
        AiUsageParser.Read("""{"usage":"unavailable"}""").Should().Be((0L, 0L, 0L));
        AiUsageParser.Read("""{"usage":{"prompt_tokens":"many"}}""").Should().Be((0L, 0L, 0L));
    }

    [Fact]
    public void A_negative_count_is_treated_as_zero()
    {
        // Negative usage would credit the customer for making a request.
        var (input, _, _) = AiUsageParser.Read("""{"usage":{"prompt_tokens":-5}}""");

        input.Should().Be(0);
    }

    [Fact]
    public void A_streamed_frame_carrying_usage_is_read_the_same_way()
    {
        // Providers put usage on one of the last frames rather than after [DONE], which is why the
        // streaming path inspects every chunk with this same parser.
        var frame = """{"id":"x","choices":[{"delta":{}}],"usage":{"prompt_tokens":10,"completion_tokens":20}}""";

        AiUsageParser.Read(frame).Should().Be((10L, 20L, 0L));
    }

    [Fact]
    public void An_ordinary_delta_frame_reports_nothing()
    {
        AiUsageParser.Read("""{"choices":[{"delta":{"content":"hello"}}]}""").Should().Be((0L, 0L, 0L));
    }
}
