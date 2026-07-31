using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who can see, use and change a template.
///
/// A template runs someone else's container image inside a tenant's private network, next to their
/// database. Appearing in the shared catalog is therefore a decision a person makes, not a side
/// effect of saving a form.
/// </summary>
public class TemplateCatalogTests
{
    private static readonly Guid Mine = Guid.NewGuid();
    private static readonly Guid Theirs = Guid.NewGuid();

    private static AppTemplate Template(Guid? owner, TemplateStatus status = TemplateStatus.Private, bool enabled = true) =>
        new() { Name = "t", WorkspaceId = owner, Status = status, IsEnabled = enabled };

    [Fact]
    public void A_template_someone_else_wrote_is_invisible_until_it_is_approved()
    {
        // The whole point. Anything else means one tenant's unreviewed image is offered to another.
        TemplateCatalog.IsVisibleTo(Template(Theirs), Mine).Should().BeFalse();
        TemplateCatalog.IsVisibleTo(Template(Theirs, TemplateStatus.Submitted), Mine).Should().BeFalse();

        TemplateCatalog.IsVisibleTo(Template(Theirs, TemplateStatus.Approved), Mine).Should().BeTrue();
    }

    [Fact]
    public void The_templates_harbora_ships_are_visible_to_everyone()
    {
        TemplateCatalog.IsVisibleTo(Template(owner: null), Mine).Should().BeTrue();
    }

    [Fact]
    public void Your_own_template_stays_usable_while_it_waits_and_after_it_is_sent_back()
    {
        // You wrote it and already trust it; review is about offering it to others.
        TemplateCatalog.IsVisibleTo(Template(Mine), Mine).Should().BeTrue();
        TemplateCatalog.IsVisibleTo(Template(Mine, TemplateStatus.Submitted), Mine).Should().BeTrue();
        TemplateCatalog.IsVisibleTo(Template(Mine, TemplateStatus.Rejected), Mine).Should().BeTrue();
    }

    [Fact]
    public void A_disabled_template_is_visible_to_nobody()
    {
        // The switch that takes a template out of circulation has to work on its own author too,
        // or it is not a switch.
        TemplateCatalog.IsVisibleTo(Template(Mine, enabled: false), Mine).Should().BeFalse();
        TemplateCatalog.IsVisibleTo(Template(null, enabled: false), Mine).Should().BeFalse();
        TemplateCatalog.IsInSharedCatalog(Template(Theirs, TemplateStatus.Approved, enabled: false)).Should().BeFalse();
    }

    [Fact]
    public void The_shared_catalog_is_narrower_than_what_you_can_use()
    {
        // Your own unreviewed template is usable by you and is not in the catalog.
        TemplateCatalog.IsInSharedCatalog(Template(Mine)).Should().BeFalse();
        TemplateCatalog.IsInSharedCatalog(Template(Mine, TemplateStatus.Approved)).Should().BeTrue();
        TemplateCatalog.IsInSharedCatalog(Template(null)).Should().BeTrue();
    }

    [Fact]
    public void An_approved_template_cannot_be_edited_behind_the_approval()
    {
        // Otherwise review means nothing: submit something harmless, get approved, change it after.
        TemplateCatalog.CanEdit(Template(Mine, TemplateStatus.Approved), Mine).Should().BeFalse();

        TemplateCatalog.CanEdit(Template(Mine), Mine).Should().BeTrue();
        TemplateCatalog.CanEdit(Template(Mine, TemplateStatus.Rejected), Mine).Should().BeTrue();
    }

    [Fact]
    public void Nobody_edits_a_template_they_do_not_own()
    {
        TemplateCatalog.CanEdit(Template(Theirs), Mine).Should().BeFalse();
        TemplateCatalog.CanEdit(Template(owner: null), Mine).Should().BeFalse("the shipped ones belong to the platform");
    }

    [Fact]
    public void A_template_is_only_submitted_from_a_state_where_review_is_the_next_step()
    {
        TemplateCatalog.CanSubmit(Template(Mine), Mine).Should().BeTrue();
        TemplateCatalog.CanSubmit(Template(Mine, TemplateStatus.Rejected), Mine).Should().BeTrue("after fixing it");

        TemplateCatalog.CanSubmit(Template(Mine, TemplateStatus.Submitted), Mine).Should().BeFalse("already waiting");
        TemplateCatalog.CanSubmit(Template(Mine, TemplateStatus.Approved), Mine).Should().BeFalse();
        TemplateCatalog.CanSubmit(Template(Theirs), Mine).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, TemplateStatus.Private, "Built in")]
    [InlineData(false, TemplateStatus.Approved, "In the shared catalog")]
    [InlineData(false, TemplateStatus.Submitted, "Waiting for review")]
    [InlineData(false, TemplateStatus.Rejected, "Sent back")]
    [InlineData(false, TemplateStatus.Private, "Yours only")]
    public void Every_state_says_what_it_means(bool? owned, TemplateStatus status, string expected)
    {
        var template = Template(owned is null ? null : Mine, status);

        TemplateCatalog.Describe(template).Should().Be(expected);
    }
}
