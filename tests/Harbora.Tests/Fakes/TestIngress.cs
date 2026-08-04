using Harbora.Infrastructure.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Harbora.Tests.Fakes;

/// <summary>
/// A real <see cref="NodeIngressRegistry"/> for tests that only need one to exist.
///
/// <para>
/// Real rather than faked because it binds nothing until something is actually placed on a tunnelled
/// node — so the cost of using the genuine article here is zero, and the tests that do exercise
/// binding exercise the same object the panel runs.
/// </para>
/// </summary>
public static class TestIngress
{
    /// <summary>A registry over a high, unused port range so a parallel run cannot collide.</summary>
    public static NodeIngressRegistry Registry(int start = 47000, int end = 47099) =>
        new(Options.Create(new NodeAgentControlPlaneOptions
        {
            IngressPortStart = start,
            IngressPortEnd = end,
        }),
        NullLogger<NodeIngressRegistry>.Instance);
}
