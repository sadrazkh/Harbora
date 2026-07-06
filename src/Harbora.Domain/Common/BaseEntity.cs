namespace Harbora.Domain.Common;

/// <summary>
/// Root type for all persisted aggregates. Uses GUIDv7-friendly Guids created in
/// the application layer so entities are sortable and safe to expose in URLs.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
