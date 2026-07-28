using System.Collections.Concurrent;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Process-local map of running job id → its <see cref="CancellationTokenSource"/>. Singleton: the
/// worker registers a job while it executes and unregisters on completion, so a cancel request
/// arriving on a web request thread can interrupt work already in progress.
/// </summary>
public sealed class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public IDisposable Register(Guid jobId, CancellationTokenSource cts)
    {
        _running[jobId] = cts;
        return new Registration(this, jobId);
    }

    public bool TryCancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The job finished between the lookup and the cancel — nothing left to stop.
            return false;
        }
    }

    private void Unregister(Guid jobId) => _running.TryRemove(jobId, out _);

    private sealed class Registration(JobCancellationRegistry owner, Guid jobId) : IDisposable
    {
        public void Dispose() => owner.Unregister(jobId);
    }
}
