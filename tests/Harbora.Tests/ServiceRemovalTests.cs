using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What deleting a database says before it does it.
///
/// The button this replaces asked "Remove service?" in a browser dialog, removed the database
/// without checking which apps were relying on it, and left the volume on the node with nothing
/// tracking it — while the code comment said the data was safely kept.
/// </summary>
public class ServiceRemovalTests
{
    [Fact]
    public void Keeping_the_data_names_the_volume_it_is_left_in()
    {
        // "Your data is kept" is not true in any useful sense if nothing says where it went: the row
        // that knew the volume name is the one being deleted.
        var plan = ServiceRemovalPlan.Describe(deleteData: false, "harbora-vol-shop-db", []);

        plan.OrphanedVolume.Should().Be("harbora-vol-shop-db",
            "the screen can only name the volume if the plan carries it");
    }

    [Fact]
    public void Deleting_the_data_says_so_plainly_and_leaves_no_volume()
    {
        var plan = ServiceRemovalPlan.Describe(deleteData: true, "harbora-vol-shop-db", []);

        plan.DeletesData.Should().BeTrue();
        plan.OrphanedVolume.Should().BeNull("nothing is left behind to find");
    }

    [Fact]
    public void The_apps_that_will_break_are_part_of_the_plan()
    {
        var plan = ServiceRemovalPlan.Describe(deleteData: false, "vol", ["shop", "worker"]);

        plan.BreaksApps.Should().BeEquivalentTo(["shop", "worker"]);
    }

    [Fact]
    public void Typing_the_name_is_required_only_when_the_data_goes_too()
    {
        // Asking for a typed confirmation on a reversible action teaches people to type it without
        // reading, which is how the irreversible one gets confirmed by reflex.
        ServiceRemovalPlan.IsConfirmed(deleteData: false, typedName: null, "shop-db").Should().BeTrue();

        ServiceRemovalPlan.IsConfirmed(deleteData: true, typedName: null, "shop-db").Should().BeFalse();
        ServiceRemovalPlan.IsConfirmed(deleteData: true, typedName: "shop-db", "shop-db").Should().BeTrue();
    }

    [Fact]
    public void A_near_miss_is_not_a_confirmation()
    {
        // Case and stray whitespace are the two ways a name gets "typed" without being read. Only
        // the whitespace is forgiven.
        ServiceRemovalPlan.IsConfirmed(true, "Shop-DB", "shop-db").Should().BeFalse("case must match");
        ServiceRemovalPlan.IsConfirmed(true, "shop", "shop-db").Should().BeFalse();
        ServiceRemovalPlan.IsConfirmed(true, "  shop-db  ", "shop-db").Should().BeTrue();
    }

    [Fact]
    public void A_service_with_no_volume_does_not_promise_data_that_does_not_exist()
    {
        var plan = ServiceRemovalPlan.Describe(deleteData: false, "", []);

        plan.OrphanedVolume.Should().BeNull("there is no data to promise anything about");
    }
}
