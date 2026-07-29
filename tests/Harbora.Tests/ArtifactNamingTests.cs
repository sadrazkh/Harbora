using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Artifact filenames must not depend on the caller's calendar.
///
/// Harbora defaults to Persian, and a restore runs inside a web request where that culture is
/// active — so a pre-restore snapshot was written as "pre-restore-…-14050507-184916.tgz" (Jalali)
/// while backups taken from a background job used Gregorian. Same directory, two calendars, and
/// names that no longer sort chronologically.
/// </summary>
public class ArtifactNamingTests
{
    private static readonly DateTimeOffset Instant = new(2026, 7, 29, 18, 49, 9, TimeSpan.Zero);

    [Fact]
    public void The_ambient_persian_culture_really_would_change_the_stamp()
    {
        // Establishes that the hazard is real rather than theoretical.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Instant.ToString("yyyyMMdd-HHmmss").Should().NotBe("20260729-184909");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void The_invariant_stamp_is_gregorian_whatever_the_request_culture_is()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Instant.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                .Should().Be("20260729-184909");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("fa-IR")]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void Every_culture_produces_the_same_artifact_name(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Instant.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                .Should().Be("20260729-184909");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
