using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether a one-off command actually runs.
///
/// Found on a live server, not in review: a release task on an image with an <c>ENTRYPOINT</c> was
/// handed to Docker as a command, which makes it <i>arguments</i> to that entrypoint. The image
/// ignored them, started its normal process, and the deployment waited on a container that was never
/// going to exit. Nothing failed; the migration simply never ran and the screen said "in progress"
/// indefinitely. These pin the rule that prevents it.
/// </summary>
public class OneOffLaunchTests
{
    [Fact]
    public void A_command_replaces_the_images_entrypoint_rather_than_being_passed_to_it()
    {
        // The whole bug in one assertion: "sh" has to BE the entrypoint, not an argument to one.
        var (entrypoint, arguments) = OneOffLaunch.From(["sh", "-c", "dotnet ef database update"]);

        entrypoint.Should().BeEquivalentTo(["sh"]);
        arguments.Should().BeEquivalentTo(["-c", "dotnet ef database update"]);
    }

    [Fact]
    public void A_single_word_command_is_sent_with_no_arguments_at_all()
    {
        // Not an empty list: Docker fills an unset command from the image, and an empty one would
        // leave it ambiguous which of the two we meant.
        var (entrypoint, arguments) = OneOffLaunch.From(["pg_dump"]);

        entrypoint.Should().BeEquivalentTo(["pg_dump"]);
        arguments.Should().BeNull("the image's own CMD must not be appended as stray arguments");
    }

    [Fact]
    public void No_command_leaves_the_image_to_run_as_its_author_intended()
    {
        var (entrypoint, arguments) = OneOffLaunch.From([]);

        entrypoint.Should().BeNull();
        arguments.Should().BeNull();
    }

    [Fact]
    public void Arguments_keep_their_order_and_their_spaces()
    {
        // A shell command arrives as one argument containing spaces; splitting or reordering it
        // would run something other than what was typed.
        var (_, arguments) = OneOffLaunch.From(["sh", "-c", "cd /app && npm run migrate"]);

        arguments.Should().BeEquivalentTo(["-c", "cd /app && npm run migrate"],
            options => options.WithStrictOrdering());
    }
}
