using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Telling a volume archive from a database export, years after either was made.
///
/// Both shapes live in the same history: database backups used to be tar archives of the data
/// directory and are now logical dumps. Restoring one as the other would untar a SQL file into a
/// data directory, or hand a tarball to psql. The file name is the only record of how it was made.
/// </summary>
public class BackupArtifactTests
{
    [Theory]
    [InlineData("database-shop-20260731-120000.tgz")]
    [InlineData("database-shop-20260731-120000.tgz.enc")]
    [InlineData("volume-uploads-20260731-120000.tar.gz")]
    public void A_tar_archive_is_restored_the_way_it_was_made(string artifact)
    {
        BackupArtifact.IsVolumeArchive(artifact).Should().BeTrue();
    }

    [Theory]
    [InlineData("database-shop-20260731-120000.sql.gz")]
    [InlineData("database-shop-20260731-120000.sql.gz.enc")]
    [InlineData("database-shop-20260731-120000.archive.gz")]
    public void A_logical_dump_is_put_back_by_the_engine_that_produced_it(string artifact)
    {
        BackupArtifact.IsVolumeArchive(artifact).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_artifact_with_no_name_is_treated_as_the_older_shape(string? artifact)
    {
        // Every backup that predates this distinction was a tar. Guessing "logical dump" for an
        // unreadable name would feed a tarball to psql.
        BackupArtifact.IsVolumeArchive(artifact).Should().BeTrue();
    }
}
