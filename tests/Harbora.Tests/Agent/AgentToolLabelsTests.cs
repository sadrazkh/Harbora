using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Infrastructure.Agent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The names people read for the agent's tools, held against the registry the panel really builds —
/// not against a list written here, which would go on passing the day a tool was added without a label.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AgentToolLabelsTests(HarboraHttpFixture fixture)
{
    private static readonly Regex PersianLetter = new(@"[؀-ۿ]");

    private IReadOnlyList<IAgentTool> RealTools()
    {
        using var scope = fixture.Panel.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentToolRegistry>().All;
    }

    [Fact]
    public void Every_tool_in_the_real_registry_has_a_label_and_no_label_names_a_tool_that_is_not_there()
    {
        var names = RealTools().Select(t => t.Name).ToList();

        names.Should().HaveCount(29, "6 read tools, 13 app tools and 10 project and database tools");
        AgentToolLabels.All.Keys.Should().BeEquivalentTo(names,
            "a tool without a label would show the model's identifier, and a label without a tool is a typo");
    }

    [Fact]
    public void A_label_is_words_for_people_in_both_languages_and_never_the_tool_name()
    {
        foreach (var tool in RealTools())
        {
            var label = AgentToolLabels.For(tool.Name);

            label.En.Should().NotBeNullOrWhiteSpace().And.NotBe(tool.Name, $"{tool.Name} shows its own identifier");
            label.En.Should().NotContain("_", $"{tool.Name}'s label reads like an identifier");
            label.Fa.Should().NotBe(tool.Name);
            PersianLetter.IsMatch(label.Fa).Should().BeTrue($"{tool.Name}'s Persian label must be Persian: {label.Fa}");
        }
    }

    [Fact]
    public void Every_category_is_one_the_pane_has_an_icon_for()
    {
        var constants = typeof(AgentToolLabels.Categories)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        foreach (var tool in RealTools())
            constants.Should().Contain(AgentToolLabels.For(tool.Name).Category, $"{tool.Name}'s category");
    }

    [Fact]
    public void The_words_the_brief_names_are_the_ones_used()
    {
        AgentToolLabels.For("get_deployment").Should().Be(new AgentToolLabel(
            "Reading the deployment's logs", "خواندن لاگ‌های استقرار", AgentToolLabels.Categories.Deployment));
        AgentToolLabels.For("list_resources").Should().Be(new AgentToolLabel(
            "Looking through your projects and resources", "مرور پروژه‌ها و منابع", AgentToolLabels.Categories.Resources));
        AgentToolLabels.For("create_database").Should().Be(new AgentToolLabel(
            "Creating a database", "ساخت دیتابیس", AgentToolLabels.Categories.Database));
    }

    [Fact]
    public void A_tool_nobody_labelled_falls_back_to_its_identifier_under_other()
    {
        AgentToolLabels.For("make_coffee").Should().Be(new AgentToolLabel("make_coffee", "make_coffee", AgentToolLabels.Categories.Other));
    }
}
