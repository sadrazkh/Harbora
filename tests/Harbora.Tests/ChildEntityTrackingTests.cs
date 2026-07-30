using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Adding a child to an app that is already loaded.
///
/// <see cref="Harbora.Domain.Common.BaseEntity"/> assigns its own Id, so every new entity arrives with
/// its key already populated. EF's default for a Guid key is "the store generates it", and under that
/// assumption a key that already has a value can only mean the row exists — so a child discovered on a
/// tracked parent was tracked as Modified and saved as an UPDATE matching no row.
///
/// Observed in production: adding a domain to an existing app returned a 500
/// (DbUpdateConcurrencyException, "expected to affect 1 row(s), but actually affected 0"). Creating an
/// app hid the bug, because db.Apps.Add cascades Added through the whole graph.
/// </summary>
public class ChildEntityTrackingTests
{
    private static HarboraDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task<Guid> SeedAppAsync(string db)
    {
        await using var ctx = NewContext(db);
        var app = new App { WorkspaceId = Guid.CreateVersion7(), Name = "app", Slug = "app" };
        ctx.Apps.Add(app);
        await ctx.SaveChangesAsync();
        return app.Id;
    }

    [Fact]
    public async Task A_domain_added_to_a_loaded_app_is_inserted()
    {
        var db = "child-domain-" + Guid.NewGuid();
        var appId = await SeedAppAsync(db);

        await using (var ctx = NewContext(db))
        {
            var app = await ctx.Apps.Include(a => a.Domains).FirstAsync(a => a.Id == appId);
            app.Domains.Add(new DomainName { Host = "app.example.com" });

            // The state is the defect. Asserting it directly says which mistake was made, rather than
            // leaving a provider-specific "0 rows affected" to be decoded later. Detection has to be
            // triggered the way SaveChanges triggers it — until then the child is merely Detached.
            ctx.ChangeTracker.DetectChanges();
            ctx.Entry(app.Domains.Single()).State.Should().Be(EntityState.Added);

            await ctx.SaveChangesAsync();
        }

        await using var check = NewContext(db);
        check.Domains.Should().ContainSingle(d => d.Host == "app.example.com" && d.AppId == appId);
    }

    [Fact]
    public async Task An_environment_variable_added_to_a_loaded_app_is_inserted()
    {
        // Same shape, different collection: the fix has to be a property of the model, not of one
        // controller action, or the next collection added re-creates the bug.
        var db = "child-env-" + Guid.NewGuid();
        var appId = await SeedAppAsync(db);

        await using (var ctx = NewContext(db))
        {
            var app = await ctx.Apps.Include(a => a.EnvironmentVariables).FirstAsync(a => a.Id == appId);
            app.EnvironmentVariables.Add(new EnvironmentVariable { Key = "TOKEN", Value = "x" });
            await ctx.SaveChangesAsync();
        }

        await using var check = NewContext(db);
        check.EnvironmentVariables.Should().ContainSingle(e => e.Key == "TOKEN" && e.AppId == appId);
    }

    [Fact]
    public async Task An_app_still_gets_the_id_it_generated()
    {
        // Guards the other direction: telling EF the store does not generate keys must not make it
        // start generating them, or ids in URLs would stop matching the rows they name.
        var db = "child-id-" + Guid.NewGuid();
        var app = new App { WorkspaceId = Guid.CreateVersion7(), Name = "app", Slug = "app" };
        var chosen = app.Id;

        await using (var ctx = NewContext(db))
        {
            ctx.Apps.Add(app);
            await ctx.SaveChangesAsync();
        }

        chosen.Should().NotBe(Guid.Empty);
        await using var check = NewContext(db);
        check.Apps.Should().ContainSingle(a => a.Id == chosen);
    }
}
