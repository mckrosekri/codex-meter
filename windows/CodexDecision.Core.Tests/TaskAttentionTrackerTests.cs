using CodexDecision.Core.Conversations;

namespace CodexDecision.Core.Tests;

public sealed class TaskAttentionTrackerTests
{
    [Fact]
    public void MarksCompletedRunsReadyUntilReviewed()
    {
        var tracker = new TaskAttentionTracker();
        var taskId = Guid.NewGuid();

        tracker.Observe(Snapshot(taskId, TaskRunState.Running));
        var changed = tracker.Observe(Snapshot(taskId, TaskRunState.Completed));

        Assert.True(changed);
        Assert.Equal(TaskAttentionKind.Ready, tracker.GetAttention(taskId));
        Assert.True(tracker.MarkReviewed(taskId));
        Assert.Equal(TaskAttentionKind.None, tracker.GetAttention(taskId));
    }

    [Fact]
    public void MarksFailuresSeparatelyFromSuccessfulCompletion()
    {
        var tracker = new TaskAttentionTracker();
        var taskId = Guid.NewGuid();

        tracker.Observe(Snapshot(taskId, TaskRunState.WaitingForInput));
        tracker.Observe(Snapshot(taskId, TaskRunState.Failed));

        Assert.Equal(TaskAttentionKind.Failed, tracker.GetAttention(taskId));
    }

    [Fact]
    public void NewRunClearsPreviousAttention()
    {
        var tracker = new TaskAttentionTracker();
        var taskId = Guid.NewGuid();

        tracker.Observe(Snapshot(taskId, TaskRunState.Running));
        tracker.Observe(Snapshot(taskId, TaskRunState.Completed));
        tracker.Observe(Snapshot(taskId, TaskRunState.Starting));

        Assert.Equal(TaskAttentionKind.None, tracker.GetAttention(taskId));
    }

    [Fact]
    public void InitialTerminalSnapshotDoesNotCreateHistoricalAttention()
    {
        var tracker = new TaskAttentionTracker();
        var taskId = Guid.NewGuid();

        tracker.Observe(Snapshot(taskId, TaskRunState.Completed));

        Assert.Equal(TaskAttentionKind.None, tracker.GetAttention(taskId));
    }

    private static TaskRuntimeSnapshot Snapshot(Guid taskId, TaskRunState state) => new(
        Guid.NewGuid(),
        taskId,
        "thread-1",
        null,
        state,
        DateTimeOffset.UtcNow,
        0,
        false,
        false,
        null,
        ContextUsageCalculator.Empty,
        null);
}
