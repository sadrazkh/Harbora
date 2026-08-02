using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// A managed-service engine that provisions nothing.
///
/// The dashboard reads <see cref="Catalog"/> to decide which database tiles to offer, so a test that
/// builds the controller needs one. Everything else throws rather than returning a plausible empty
/// answer: a test that reaches those has wandered somewhere it did not mean to, and should say so.
/// </summary>
public sealed class FakeManagedServiceEngine(IReadOnlyList<ServiceCatalogEntry>? catalog = null)
    : IManagedServiceEngine
{
    public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = catalog ?? [];

    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
}
