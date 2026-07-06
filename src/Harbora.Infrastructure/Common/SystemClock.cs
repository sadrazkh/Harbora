using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Common;

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
