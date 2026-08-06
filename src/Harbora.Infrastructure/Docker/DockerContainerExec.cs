using Docker.DotNet;
using Docker.DotNet.Models;
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
internal sealed class DockerContainerExec(IDockerClient client, string execId, MultiplexedStream stream)
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

    public async Task ResizeAsync(uint columns, uint rows, CancellationToken ct)
    {
        // A resize that fails is not a reason to end somebody's session — the shell keeps running at
        // whatever size it had, which is a wrongly-drawn screen rather than a lost one.
        try
        {
            await client.Exec.ResizeContainerExecTtyAsync(execId,
                new ContainerResizeParameters { Width = (long)columns, Height = (long)rows }, ct);
        }
        catch (DockerApiException) { }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
