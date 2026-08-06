using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Action parameters that share a name with a route value.
///
/// MVC binds a parameter from route values before it looks at the query string, and every route in
/// this application carries <c>controller</c> and <c>action</c> — the names of the thing being
/// invoked. A parameter called <c>action</c> therefore never sees <c>?action=…</c>; it silently
/// receives the method's own name.
///
/// The audit log was written this way. Its filter compared <c>Action == "Index"</c> on every request
/// and its CSV export compared <c>Action == "Export"</c>, so both were empty from the day the page
/// shipped — while the filter dropdown beside them, filled by a separate unfiltered query, listed
/// twenty-four kinds of event the log plainly had records of. Nothing threw, nothing was logged, and
/// the page looked like a platform where nothing had ever happened.
///
/// Naming a binding source is the fix: <c>[FromQuery]</c> stops route values being considered.
/// </summary>
public class RouteValueCollisionTests
{
    /// <summary>Names the routing layer puts in route values on every request.</summary>
    private static readonly string[] RouteValueNames = ["action", "controller", "area", "handler"];

    private static IEnumerable<(Type Controller, MethodInfo Method)> Actions()
    {
        var assembly = typeof(Harbora.Web.Controllers.AuditController).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => typeof(Controller).IsAssignableFrom(t)))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (!method.IsSpecialName)
                    yield return (type, method);
    }

    [Fact]
    public void The_scan_finds_the_controllers()
    {
        // Guards the test below: an empty scan makes it pass without checking anything.
        Actions().Should().HaveCountGreaterThan(50);
    }

    [Fact]
    public void No_action_parameter_silently_takes_its_value_from_the_route()
    {
        var offenders = new List<string>();

        foreach (var (controller, method) in Actions())
            foreach (var parameter in method.GetParameters())
            {
                if (!RouteValueNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                // A declared source — [FromQuery], [FromRoute], [FromForm] — is a decision. Its
                // absence is the accident.
                var declared = parameter.GetCustomAttributes()
                    .Any(a => a is Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata);

                if (!declared)
                    offenders.Add($"{controller.Name}.{method.Name}({parameter.Name})");
            }

        offenders.Should().BeEmpty(
            "a parameter named after a route value binds from the route, not the query string — "
            + "declare [FromQuery] (and rename it) so the value somebody sent is the value it gets");
    }
}
