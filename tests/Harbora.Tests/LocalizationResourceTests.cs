using System.Globalization;
using System.Resources;
using FluentAssertions;
using Harbora.Web;
using Xunit;

namespace Harbora.Tests;

public class LocalizationResourceTests
{
    [Fact]
    public void PersianSharedResource_IsDiscoverableFromTheMarkerType()
    {
        typeof(SharedResource).Namespace.Should().Be("Harbora.Web");

        var resources = new ResourceManager(
            "Harbora.Web.Resources.SharedResource",
            typeof(SharedResource).Assembly);

        resources.GetString("Applications", CultureInfo.GetCultureInfo("fa"))
            .Should().Be("اپلیکیشن‌ها");
        resources.GetString("Create Application", CultureInfo.GetCultureInfo("fa"))
            .Should().Be("ساخت اپلیکیشن");
    }
}
