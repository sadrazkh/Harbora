namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Reports on the calling thread instead of posting to the thread pool.
///
/// <see cref="Progress{T}"/> dispatches asynchronously, which is right for a UI and wrong for
/// capturing a command's output: by the time the call that produced the output returns, the last
/// lines may not have been handed over yet. Both places that read captured output immediately — a
/// release task's failure message and a scheduled job's recorded output — lost the final lines that
/// way. A cron run showed the image pull and nothing the job itself printed, which is precisely the
/// question run history exists to answer.
///
/// The handler runs on whichever thread produced the value, so it must be safe to call from the
/// container engine's threads.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
