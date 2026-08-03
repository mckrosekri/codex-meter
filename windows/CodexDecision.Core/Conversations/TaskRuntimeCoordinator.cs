using System.Collections.Concurrent;
using System.Text.Json;
using CodexDecision.Core.AgentBackends;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Conversations;

public enum TaskRunState
{
    Idle,
    Starting,
    Running,
    WaitingForApproval,
    WaitingForInput,
    Paused,
    Stopping,
    Recovering,
    Disconnected,
    Completed,
    Failed,
}

public enum ContextPressure
{
    Unknown,
    Normal,
    Advisory,
    High,
    Critical,
}

public sealed record ContextUsageSnapshot(
    long TotalTokens,
    long? ModelContextWindow,
    double? UsedPercent,
    ContextPressure Pressure,
    bool IsCompacting = false);

public sealed record TaskRuntimeSnapshot(
    Guid ProjectId,
    Guid TaskId,
    string? ThreadId,
    string? TurnId,
    TaskRunState State,
    DateTimeOffset LastActivityAt,
    int QueueCount,
    bool QueuePaused,
    bool IsQuiet,
    RoutingDecision? Route,
    ContextUsageSnapshot Context,
    string? Error);

public sealed class TaskRuntimeSession
{
    private readonly Lock gate = new();
    private TaskRuntimeSnapshot snapshot;

    internal TaskRuntimeSession(Guid projectId, Guid taskId, string? threadId)
    {
        Queue = new FollowUpQueue();
        snapshot = new TaskRuntimeSnapshot(
            projectId,
            taskId,
            threadId,
            null,
            TaskRunState.Idle,
            DateTimeOffset.UtcNow,
            0,
            false,
            false,
            null,
            ContextUsageCalculator.Empty,
            null);
        Queue.Changed += (_, _) => Mutate(current => current with
        {
            QueueCount = Queue.Snapshot().Count,
            LastActivityAt = DateTimeOffset.UtcNow,
        });
    }

    public event EventHandler<TaskRuntimeSnapshot>? Changed;

    public FollowUpQueue Queue { get; }

    public TaskRuntimeSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void AttachThread(string? threadId) => Mutate(current => current with
    {
        ThreadId = threadId,
        LastActivityAt = DateTimeOffset.UtcNow,
    });

    public void AttachTurn(string? turnId) => Mutate(current => current with
    {
        TurnId = turnId,
        LastActivityAt = DateTimeOffset.UtcNow,
    });

    public void SetRoute(RoutingDecision? route) => Mutate(current => current with { Route = route });

    public void SetState(TaskRunState state, string? error = null) => Mutate(current => current with
    {
        State = state,
        Error = error,
        LastActivityAt = DateTimeOffset.UtcNow,
        IsQuiet = false,
    });

    public void SetQueuePaused(bool paused) => Mutate(current => current with { QueuePaused = paused });

    public void SetContext(ContextUsageSnapshot context) => Mutate(current => current with
    {
        Context = context,
        LastActivityAt = DateTimeOffset.UtcNow,
        IsQuiet = false,
    });

    public void Touch() => Mutate(current => current with
    {
        LastActivityAt = DateTimeOffset.UtcNow,
        IsQuiet = false,
    });

    internal void SetQuiet(bool quiet) => Mutate(current => current with { IsQuiet = quiet });

    private void Mutate(Func<TaskRuntimeSnapshot, TaskRuntimeSnapshot> mutation)
    {
        TaskRuntimeSnapshot next;
        lock (gate)
        {
            next = mutation(snapshot);
            if (next == snapshot)
            {
                return;
            }

            snapshot = next;
        }

        Changed?.Invoke(this, next);
    }
}

public sealed class TaskRuntimeCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, TaskRuntimeSession> sessions = new();

    public event EventHandler<TaskRuntimeSnapshot>? SnapshotChanged;

    public TaskRuntimeSession GetOrCreate(Guid projectId, Guid taskId, string? threadId = null)
    {
        var session = sessions.GetOrAdd(taskId, _ =>
        {
            var created = new TaskRuntimeSession(projectId, taskId, threadId);
            created.Changed += (_, snapshot) => SnapshotChanged?.Invoke(this, snapshot);
            return created;
        });
        if (!string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(session.Snapshot.ThreadId))
        {
            session.AttachThread(threadId);
        }

        return session;
    }

    public TaskRuntimeSession? Find(Guid taskId) => sessions.GetValueOrDefault(taskId);

    public TaskRuntimeSession? FindByThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return sessions.Values.FirstOrDefault(session =>
            string.Equals(session.Snapshot.ThreadId, threadId, StringComparison.Ordinal));
    }

    public IReadOnlyList<TaskRuntimeSnapshot> SnapshotAll() =>
        sessions.Values.Select(session => session.Snapshot).ToArray();

    public void HandleConnectionState(AgentBackendSnapshot connection)
    {
        foreach (var session in sessions.Values.Where(candidate =>
                     candidate.Snapshot.State is not TaskRunState.Idle and
                         not TaskRunState.Completed and not TaskRunState.Failed))
        {
            session.SetState(connection.State switch
            {
                AgentBackendState.Recovering or AgentBackendState.Connecting => TaskRunState.Recovering,
                AgentBackendState.Failed => TaskRunState.Disconnected,
                _ => session.Snapshot.State,
            }, connection.Error);
        }
    }

    public void HandleNotification(AppServerEvent notification)
    {
        var threadId = ReadString(notification.Parameters, "threadId")
                       ?? ReadString(notification.Parameters, "conversationId");
        var session = FindByThread(threadId);
        if (session is null)
        {
            return;
        }

        session.Touch();
        switch (notification.Method)
        {
            case "turn/started":
                session.AttachTurn(ReadString(notification.Parameters, "turn", "id")
                                   ?? ReadString(notification.Parameters, "turnId"));
                session.SetState(TaskRunState.Running);
                break;
            case "turn/completed":
                session.AttachTurn(null);
                var status = ReadString(notification.Parameters, "turn", "status")
                             ?? ReadString(notification.Parameters, "status");
                session.SetState(status is "failed" or "error"
                    ? TaskRunState.Failed
                    : TaskRunState.Completed,
                    status is "failed" or "error" ? "The Codex turn failed." : null);
                break;
            case "thread/status/changed":
                ApplyThreadStatus(session, notification.Parameters);
                break;
            case "thread/tokenUsage/updated":
                ApplyTokenUsage(session, notification.Parameters);
                break;
            case "thread/compacted":
            case "thread/compact/completed":
                session.SetContext(session.Snapshot.Context with { IsCompacting = false });
                break;
            case "thread/compact/started":
                session.SetContext(session.Snapshot.Context with { IsCompacting = true });
                break;
        }
    }

    public IReadOnlyList<TaskRuntimeSnapshot> UpdateQuietState(
        DateTimeOffset now,
        TimeSpan? quietThreshold = null)
    {
        var threshold = quietThreshold ?? TimeSpan.FromMinutes(5);
        var changed = new List<TaskRuntimeSnapshot>();
        foreach (var session in sessions.Values)
        {
            var current = session.Snapshot;
            var quiet = current.State is TaskRunState.Running && now - current.LastActivityAt >= threshold;
            if (quiet != current.IsQuiet)
            {
                session.SetQuiet(quiet);
                changed.Add(session.Snapshot);
            }
        }

        return changed;
    }

    private static void ApplyThreadStatus(TaskRuntimeSession session, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("status", out var status))
        {
            return;
        }

        var type = ReadString(status, "type");
        if (type == "active")
        {
            var flags = status.TryGetProperty("activeFlags", out var activeFlags) &&
                        activeFlags.ValueKind == JsonValueKind.Array
                ? activeFlags.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .ToArray()
                : [];
            session.SetState(flags.Contains("waitingOnApproval")
                ? TaskRunState.WaitingForApproval
                : flags.Contains("waitingOnUserInput")
                    ? TaskRunState.WaitingForInput
                    : TaskRunState.Running);
            return;
        }

        session.SetState(type switch
        {
            "idle" => TaskRunState.Idle,
            "systemError" => TaskRunState.Failed,
            "notLoaded" => TaskRunState.Disconnected,
            _ => session.Snapshot.State,
        }, type == "systemError" ? "Codex reported a system error." : null);
    }

    private static void ApplyTokenUsage(TaskRuntimeSession session, JsonElement parameters)
    {
        try
        {
            var update = parameters.Deserialize<ThreadTokenUsageUpdated>(
                JsonOptions);
            if (update is not null)
            {
                session.SetContext(ContextUsageCalculator.Calculate(update.TokenUsage));
            }
        }
        catch (JsonException)
        {
            // A future protocol shape should not terminate the running task.
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string objectProperty, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectProperty, out var nested)
            ? ReadString(nested, property)
            : null;
}

public static class ContextUsageCalculator
{
    public static ContextUsageSnapshot Empty { get; } =
        new(0, null, null, ContextPressure.Unknown);

    public static ContextUsageSnapshot Calculate(ThreadTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var tokensInContext = Math.Max(
            0,
            usage.Last.TotalTokens - usage.Last.ReasoningOutputTokens);

        if (usage.ModelContextWindow is not > 0)
        {
            return new ContextUsageSnapshot(
                tokensInContext,
                usage.ModelContextWindow,
                null,
                ContextPressure.Unknown);
        }

        var percent = Math.Clamp(
            tokensInContext * 100d / usage.ModelContextWindow.Value,
            0,
            100);
        var pressure = percent switch
        {
            >= 95 => ContextPressure.Critical,
            >= 85 => ContextPressure.High,
            >= 70 => ContextPressure.Advisory,
            _ => ContextPressure.Normal,
        };
        return new ContextUsageSnapshot(
            tokensInContext,
            usage.ModelContextWindow,
            percent,
            pressure);
    }
}
