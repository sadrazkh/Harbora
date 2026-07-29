using FluentAssertions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Guards the failure that took the panel down in production: the EF model had a change (an index)
/// that the migration snapshot didn't, so <c>MigrateAsync</c> threw
/// <c>PendingModelChangesWarning</c> on every boot — before serving a single request, and with no
/// hint in the UI because there was no UI. It is trivial to cause (edit <c>OnModelCreating</c>,
/// forget to regenerate) and it fails only in production, which is the worst possible combination.
///
/// This test compares the model EF builds from the code against the snapshot the migrations
/// describe, exactly as the runtime check does — but at build time.
/// </summary>
public class MigrationConsistencyTests
{
    [Fact]
    public void The_model_has_no_changes_that_are_missing_from_a_migration()
    {
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
                .Options);

        // Same comparison the migrator performs at startup; no database connection required.
        var differ = db.GetService<IMigrationsModelDiffer>();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;

        snapshot.Should().NotBeNull("the migrations assembly must contain a model snapshot");

        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var snapshotModel = db.GetService<IModelRuntimeInitializer>()
            .Initialize(((IMutableModel)snapshot!.Model).FinalizeModel(), designTime: true, validationLogger: null);

        var differences = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel());

        differences.Should().BeEmpty(
            "the model and the migrations have diverged — run: dotnet ef migrations add <Name> " +
            "--project src/Harbora.Data --startup-project src/Harbora.Web");
    }

    [Fact]
    public void Every_migration_is_paired_with_a_designer_file()
    {
        // A migration without its .Designer.cs carries no snapshot, so the next `migrations add`
        // silently generates a diff against the wrong baseline.
        var dir = Path.Combine(FindRepoRoot(), "src", "Harbora.Data", "Migrations");
        var migrations = Directory.GetFiles(dir, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).StartsWith("HarboraDbContextModelSnapshot", StringComparison.Ordinal))
            .ToList();

        migrations.Should().NotBeEmpty();
        foreach (var migration in migrations)
        {
            var designer = migration[..^3] + ".Designer.cs";
            File.Exists(designer).Should().BeTrue($"{Path.GetFileName(migration)} needs its Designer file");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
