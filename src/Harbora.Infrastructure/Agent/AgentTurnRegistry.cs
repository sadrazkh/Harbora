namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The turns this process is answering right now, by conversation — how Stop
/// (<c>POST /assistant/conversations/{id}/stop</c>, a request of its own) reaches a turn that no longer
/// belongs to the request that started it. <see cref="AgentConversationService.SendAsync"/> registers a turn
/// once it holds the conversation and releases it when the turn's work is over.
///
/// <para>
/// <b>In memory, so one process.</b> The panel runs as a single process, which is what makes a turn and the
/// Stop meant for it always meet here. Behind a second replica, a Stop that landed on the other process would
/// find nothing and answer "nothing to stop" — the turn would then end by itself, which is what pressing Stop
/// a moment too late does anyway. Scaling the panel out means replacing this class.
/// </para>
///
/// <para>
/// The lookup, the ownership check and the stop request happen under one lock, and so does a release. A Stop
/// therefore either reaches a turn that can still be stopped — and the turn, which reads the request as it
/// releases itself, is certain to see it — or finds nothing and answers false. A turn releases itself the
/// moment nothing it has left to do can be stopped (the model has given its last word, or its plan is about to
/// be stored and claimed), so <see cref="AgentStopView.Stopped"/> true always means the answer really was
/// cut short, and a <c>stopped</c> event follows on its stream.
/// </para>
/// </summary>
public sealed class AgentTurnRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AgentTurnHandle> _turns = [];

    /// <summary>
    /// Registers a turn. A turn that took over a stale mark replaces whatever was registered before it; the
    /// predecessor's own release then removes nothing (<see cref="Release"/>).
    /// </summary>
    internal AgentTurnHandle Register(Guid conversationId, Guid workspaceId, Guid userId)
    {
        var handle = new AgentTurnHandle(conversationId, workspaceId, userId);
        lock (_gate) _turns[conversationId] = handle;
        return handle;
    }

    /// <summary>
    /// From here the turn cannot be stopped: a Stop finds nothing and answers false. Returns whether a Stop
    /// reached it first — read under the lock, so the answer is final, and the turn must then honour it.
    /// Idempotent; removes only this handle, never a newer turn's.
    /// </summary>
    internal bool Release(AgentTurnHandle handle)
    {
        lock (_gate)
        {
            if (_turns.TryGetValue(handle.ConversationId, out var current) && ReferenceEquals(current, handle))
                _turns.Remove(handle.ConversationId);
            return handle.StopRequested;
        }
    }

    /// <summary>
    /// Tells the turn running in this conversation to stop, when it is this person's in this workspace. False
    /// when there is nothing of theirs to stop — no turn, a turn already over, or somebody else's.
    /// </summary>
    public bool Stop(Guid conversationId, Guid workspaceId, Guid userId)
    {
        AgentTurnHandle? handle;
        lock (_gate)
        {
            if (!_turns.TryGetValue(conversationId, out handle)
                || handle.WorkspaceId != workspaceId || handle.UserId != userId)
                return false;
            handle.StopRequested = true;
        }

        handle.Cancel();
        return true;
    }
}

/// <summary>One registered turn: whose it is, and the token Stop cancels.</summary>
internal sealed class AgentTurnHandle(Guid conversationId, Guid workspaceId, Guid userId)
{
    // Never disposed, on purpose: it has no timer and no wait handle, so there is nothing to free, and a
    // Stop that read the handle just before its release may still be cancelling it — on a disposed source
    // that would throw into somebody else's request.
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _stopRequested;

    public Guid ConversationId { get; } = conversationId;
    public Guid WorkspaceId { get; } = workspaceId;
    public Guid UserId { get; } = userId;

    public CancellationToken Token => _stop.Token;

    /// <summary>Set under the registry's lock, before the token is cancelled — so a cancellation seen by the
    /// turn with this still false is never the person's.</summary>
    public bool StopRequested
    {
        get => _stopRequested;
        set => _stopRequested = value;
    }

    /// <summary>
    /// The flag flips at once; the callbacks run on the thread pool. The turn's token reaches into the model
    /// client and the database driver, whose cancellation can send a request of its own, and the person's
    /// Stop should not wait on that.
    /// </summary>
    public void Cancel() => _ = _stop.CancelAsync();
}
