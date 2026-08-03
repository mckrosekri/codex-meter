namespace CodexDecision.Core.Conversations;

public enum TaskAttentionKind
{
    None,
    Ready,
    Failed,
}

/// <summary>
/// Tracks task runs that finished after the user last reviewed their chat.
/// This state is intentionally session-scoped; Codex remains the source of truth
/// for the conversation itself.
/// </summary>
public sealed class TaskAttentionTracker
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, TaskRunState> previousStates = [];
    private readonly Dictionary<Guid, TaskAttentionKind> attention = [];

    public bool Observe(TaskRuntimeSnapshot snapshot)
    {
        lock (gate)
        {
            var hadPrevious = previousStates.TryGetValue(snapshot.TaskId, out var previous);
            previousStates[snapshot.TaskId] = snapshot.State;

            if (IsInFlight(snapshot.State))
            {
                return attention.Remove(snapshot.TaskId);
            }

            if (!hadPrevious || previous == snapshot.State || !IsInFlight(previous) ||
                !TryGetAttention(snapshot.State, out var next))
            {
                return false;
            }

            if (attention.TryGetValue(snapshot.TaskId, out var current) && current == next)
            {
                return false;
            }

            attention[snapshot.TaskId] = next;
            return true;
        }
    }

    public TaskAttentionKind GetAttention(Guid taskId)
    {
        lock (gate)
        {
            return attention.GetValueOrDefault(taskId);
        }
    }

    public bool MarkReviewed(Guid taskId)
    {
        lock (gate)
        {
            return attention.Remove(taskId);
        }
    }

    private static bool IsInFlight(TaskRunState state) => state is
        TaskRunState.Starting or TaskRunState.Running or TaskRunState.WaitingForApproval or
        TaskRunState.WaitingForInput or TaskRunState.Paused or TaskRunState.Stopping or
        TaskRunState.Recovering;

    private static bool TryGetAttention(TaskRunState state, out TaskAttentionKind kind)
    {
        kind = state switch
        {
            TaskRunState.Completed => TaskAttentionKind.Ready,
            TaskRunState.Failed or TaskRunState.Disconnected => TaskAttentionKind.Failed,
            _ => TaskAttentionKind.None,
        };
        return kind is not TaskAttentionKind.None;
    }
}
