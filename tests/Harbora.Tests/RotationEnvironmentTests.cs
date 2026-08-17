using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Rewriting an app's environment variables after a database password rotation.
///
/// <para>
/// The attach path already knows two databases of the same engine cannot share plain names —
/// <see cref="AttachKeys"/> writes the second one under a prefix, and <c>DatabasesController.Detach</c>
/// only removes a variable whose decrypted value still matches the database being detached. Rotation
/// (<see cref="ManagedServiceEngine.RotatePasswordAsync"/>) grew up beside both without asking either:
/// it looked for the bare key alone, so a database attached second — and therefore living only under
/// its prefixed names — kept a dead password after rotation; and it matched by key alone, so rotating
/// it could overwrite the first database's bare-named variable with its own new value.
/// </para>
/// </summary>
public class RotationEnvironmentTests
{
    [Fact]
    public async Task An_app_with_two_attached_databases_has_both_connection_strings_correct_after_rotating_one()
    {
        using var h = new RotationHarness();
        var orders = await h.SeedDatabaseAsync("orders");
        var customers = await h.SeedDatabaseAsync("customers");
        var app = await h.AttachBothAsync(orders, customers);

        await h.Engine().RotatePasswordAsync(customers.Id, default);

        var rotatedCustomers = await h.ReadServiceAsync(customers.Id);
        var expectedNewCustomersUrl = h.AttachEnvFor(rotatedCustomers)["DATABASE_URL"];
        var vars = await h.ReadEnvironmentAsync(app.Id);

        // Defect 2, in this test: customers was rotated, not orders — orders' bare-named variable
        // must not move just because the two databases happen to want the same key name.
        vars["DATABASE_URL"].Should().Be(h.AttachEnvFor(orders)["DATABASE_URL"],
            "orders was not rotated, and rotating customers must not touch the variable orders owns");

        // Defect 1, in this test: customers was attached second and only ever held its prefixed
        // names, so rotation has to reach CUSTOMERS_DATABASE_URL or the app is left with a dead
        // password under the only name it actually holds.
        vars["CUSTOMERS_DATABASE_URL"].Should().Be(expectedNewCustomersUrl,
            "customers only ever held its prefixed names, and rotation must rewrite them there");
    }

    [Fact]
    public async Task Rotating_a_database_does_not_modify_a_variable_belonging_to_a_different_database()
    {
        using var h = new RotationHarness();
        var orders = await h.SeedDatabaseAsync("orders");
        var customers = await h.SeedDatabaseAsync("customers");
        var app = await h.AttachBothAsync(orders, customers);
        var before = await h.ReadEnvironmentAsync(app.Id);

        await h.Engine().RotatePasswordAsync(customers.Id, default);

        var after = await h.ReadEnvironmentAsync(app.Id);

        foreach (var key in new[]
        {
            "DATABASE_URL", "PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE",
            "DATABASE_DSN", "ConnectionStrings__DefaultConnection"
        })
            after[key].Should().Be(before[key],
                $"{key} belongs to orders — the value it held before rotating a different database " +
                "must be exactly what it holds after");
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon, wiring up an app the way
/// two sequential attaches actually would.</summary>
internal sealed class RotationHarness : IDisposable
{
    private readonly string _database = "rotation-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly HarboraDbContext _db;

    public FakeDockerEngine Docker { get; } = new();
    public PassthroughProtector Protector { get; } = new();
    public FixedClock Clock { get; } = new();

    public RotationHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        _db.SaveChanges();
    }

    private HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    public ManagedServiceEngine Engine() => new(
        _db,
        new SingleEngineFactory(Docker),
        Protector,
        new NoopJobQueue(),
        new Harbora.Infrastructure.Billing.BillingGate(
            _db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<ManagedServiceEngine>.Instance);

    public async Task<ManagedService> SeedDatabaseAsync(string name)
    {
        var service = new ManagedService
        {
            WorkspaceId = _workspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine",
            ContainerName = $"harbora-svc-{name}",
            InternalPort = 5432,
            Username = "harbora",
            EncryptedPassword = Protector.Protect($"{name}-original-pw12"),
            DatabaseName = name,
            VolumeName = $"harbora-svc-{name}-data",
            Status = ServiceStatus.Running
        };
        _db.ManagedServices.Add(service);
        await _db.SaveChangesAsync();
        return service;
    }

    /// <summary>What <c>AttachEnv</c> produces for a database as it stands right now, decrypting its
    /// current password. Used by tests to know what a variable should read without hard-coding the
    /// randomly generated replacement password.</summary>
    public IReadOnlyDictionary<string, string> AttachEnvFor(ManagedService svc) =>
        ServiceCatalog.All[svc.Type].AttachEnv(new ServiceCreds(
            svc.ContainerName, ServiceCatalog.All[svc.Type].Port, svc.Username,
            Protector.Unprotect(svc.EncryptedPassword), svc.DatabaseName));

    /// <summary>Attaches both databases to one new app, in order — the same way the Attach action
    /// would: the first claims the bare names, the second falls back to its own prefix because
    /// <see cref="AttachKeys.For"/> sees the bare names already hold somebody else's values.</summary>
    public async Task<App> AttachBothAsync(ManagedService first, ManagedService second)
    {
        var app = new App
        {
            WorkspaceId = _workspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "web",
            Slug = "web-" + Guid.NewGuid().ToString("n")[..8],
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "alpine:3.20"
        };
        _db.Apps.Add(app);

        var firstWanted = AttachEnvFor(first);
        var firstFinal = AttachKeys.For(firstWanted, new Dictionary<string, string?>(), first.Name);
        foreach (var (key, value) in firstFinal)
            app.EnvironmentVariables.Add(new EnvironmentVariable
            { Key = key, Value = Protector.Protect(value), IsSecret = true });

        var secondWanted = AttachEnvFor(second);
        var existingForSecond = secondWanted.Keys.ToDictionary(
            k => k,
            k => app.EnvironmentVariables.FirstOrDefault(v => v.Key == k) is { } ev
                ? (string?)Protector.Unprotect(ev.Value)
                : null);
        var secondFinal = AttachKeys.For(secondWanted, existingForSecond, second.Name);
        foreach (var (key, value) in secondFinal)
            app.EnvironmentVariables.Add(new EnvironmentVariable
            { Key = key, Value = Protector.Protect(value), IsSecret = true });

        await _db.SaveChangesAsync();
        return app;
    }

    public async Task<ManagedService> ReadServiceAsync(Guid id)
    {
        using var db = Read();
        return await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadEnvironmentAsync(Guid appId)
    {
        using var db = Read();
        var app = await db.Apps.Include(a => a.EnvironmentVariables).AsNoTracking()
            .SingleAsync(a => a.Id == appId);
        return app.EnvironmentVariables.ToDictionary(v => v.Key, v => Protector.Unprotect(v.Value));
    }

    public void Dispose() => _db.Dispose();
}
