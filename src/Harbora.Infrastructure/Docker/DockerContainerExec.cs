using Docker.DotNet;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// A shell inside a local container, over docker's attached exec stream.
///
/// Thin on purpose. Everything about who may open this, what is run, how big it is and when it
/// closes lives in <see cref="Terminals.TerminalAccess"/> and in the endpoint; this only carries
/// bytes. The one piece of judgement here is what an ended stream looks like — docker signals it as
/// a read of zero with <c>EOF</c>, and treating that as "nothing right now" instead of "it is over"
/// leaves a session spinning against a shell that exited.
/// </summary>
internal sealed class DockerContainerExec(MultiplexedStream stream)
    : IContainerExec
{
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var scratch = new byte[buffer.Length];
        var result = await stream.ReadOutputAsync(scratch, 0, scratch.Length, ct);

        if (result.EOF) return 0;

        scratch.AsMemory(0, result.Count).CopyTo(buffer);
        return result.Count;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        stream.WriteAsync(data.ToArray(), 0, data.Length, ct);

    /// <summary>
    /// Unverified / known regression from the Docker.DotNet.Enhanced 3.131.1 migration (the fork
    /// this moved to for Docker 29 support). The old client's
    /// <c>IExecOperations.ResizeContainerExecTtyAsync</c> — <c>POST /exec/{id}/resize</c> — has no
    /// replacement here: decompiling the shipped assembly shows <c>IExecOperations</c> now has
    /// exactly three methods (create, start, inspect exec), none of them resize, and
    /// <c>IContainerOperations.ResizeContainerTtyAsync</c> only ever posts to
    /// <c>/containers/{id}/resize</c> — a different endpoint that would 404 against an exec ID rather
    /// than a container one. The Docker Engine API itself still has the exec-resize route; this
    /// client version just cannot reach it, and there is no other public surface on <c>IDockerClient</c>
    /// (its request-building methods are all internal) to call it directly either.
    ///
    /// A live resize (a browser window resized while the session is already open) is therefore a
    /// no-op now: the shell keeps running at whatever size it opened with — the same fallback this
    /// method already used for a resize the daemon rejected, just for every resize now rather than an
    /// occasional one. The INITIAL size is unaffected: <see cref="DockerEngine.ExecAsync"/> sets it
    /// via <c>ConsoleSize</c> on the exec create/start calls themselves, which this client version
    /// does support, so a session still opens at the right size — it just cannot be resized again
    /// afterwards.
    /// </summary>
    public Task ResizeAsync(uint columns, uint rows, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
