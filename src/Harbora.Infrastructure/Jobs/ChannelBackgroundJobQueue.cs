using System.Threading.Channels;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Bounded, in-process job queue backed by a <see cref="Channel{T}"/>. A hosted worker
/// (BackgroundJobWorker) resolves a scoped service provider per job and runs it. Redis-backed
/// distribution can replace this later without touching callers.
/// </summary>
public sealed class ChannelBackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(
            new BoundedChannelOptions(capacity: 500) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);
}
