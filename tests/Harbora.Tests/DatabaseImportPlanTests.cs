using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10: the typed-name gate in front of a self-serve database import — do-not-change list
/// item 19's idiom (<c>ServiceRemovalPlan</c>/<c>ProjectRemovalPlan</c>), applied here to an act that,
/// unlike removal, has no reversible branch that could skip the prompt.
/// </summary>
public sealed class DatabaseImportPlanTests
{
    [Fact]
    public void The_exact_name_confirms()
    {
        DatabaseImportPlan.IsConfirmed("orders", "orders").Should().BeTrue();
    }

    [Fact]
    public void A_different_name_does_not_confirm()
    {
        DatabaseImportPlan.IsConfirmed("orders-typo", "orders").Should().BeFalse();
    }

    [Fact]
    public void An_empty_typed_name_does_not_confirm()
    {
        DatabaseImportPlan.IsConfirmed("", "orders").Should().BeFalse();
        DatabaseImportPlan.IsConfirmed(null, "orders").Should().BeFalse();
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_but_case_still_matters()
    {
        DatabaseImportPlan.IsConfirmed("  orders  ", "orders").Should().BeTrue(
            "a pasted or autocompleted name commonly carries incidental whitespace");
        DatabaseImportPlan.IsConfirmed("Orders", "orders").Should().BeFalse(
            "typing the exact name is the point; a case-insensitive match would let a guess through");
    }
}
