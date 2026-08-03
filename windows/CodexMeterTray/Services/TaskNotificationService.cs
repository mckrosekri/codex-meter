using CodexDecision.Core.Conversations;
using CodexDecision.Core.Projects;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexMeterTray.Services;

public sealed class TaskNotificationService : IDisposable
{
    private readonly Func<NotificationPreference> preference;
    private readonly Func<bool> isWindowVisible;
    private readonly Action<Guid, Guid> activateTask;
    private readonly Action<string> fallback;
    private readonly Dictionary<Guid, TaskRunState> previousStates = [];
    private AppNotificationManager? manager;

    public TaskNotificationService(
        Func<NotificationPreference> preference,
        Func<bool> isWindowVisible,
        Action<Guid, Guid> activateTask,
        Action<string> fallback)
    {
        this.preference = preference;
        this.isWindowVisible = isWindowVisible;
        this.activateTask = activateTask;
        this.fallback = fallback;
    }

    public void Initialize()
    {
        try
        {
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += Manager_NotificationInvoked;
            manager.Register();
        }
        catch
        {
            manager = null;
        }
    }

    public void HandleSnapshot(TaskRuntimeSnapshot snapshot)
    {
        previousStates.TryGetValue(snapshot.TaskId, out var previous);
        previousStates[snapshot.TaskId] = snapshot.State;
        if (previous == snapshot.State || !ShouldNotify(snapshot.State))
        {
            return;
        }

        var configured = preference();
        if (configured is NotificationPreference.Never ||
            configured is NotificationPreference.BackgroundOnly && isWindowVisible())
        {
            return;
        }

        var (title, message) = snapshot.State switch
        {
            TaskRunState.WaitingForApproval => ("Codex needs approval", "A task is waiting for your permission."),
            TaskRunState.WaitingForInput => ("Codex needs input", "A task is waiting for your answer."),
            TaskRunState.Completed => ("Codex task ready", "A task finished and is ready for review."),
            TaskRunState.Disconnected => ("Codex disconnected", "A running task lost its App Server connection."),
            _ => ("Codex task failed", "A task stopped with an error."),
        };
        if (manager is null)
        {
            fallback($"{title}: {message}");
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .AddArgument("project", snapshot.ProjectId.ToString("D"))
                .AddArgument("task", snapshot.TaskId.ToString("D"))
                .BuildNotification();
            notification.Tag = snapshot.TaskId.ToString("N");
            manager.Show(notification);
        }
        catch
        {
            fallback($"{title}: {message}");
        }
    }

    public void Dispose()
    {
        if (manager is null)
        {
            return;
        }

        manager.NotificationInvoked -= Manager_NotificationInvoked;
        try
        {
            manager.Unregister();
        }
        catch
        {
        }
    }

    private void Manager_NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var values = args.Arguments;
        if (values.TryGetValue("project", out var project) &&
            values.TryGetValue("task", out var task) &&
            Guid.TryParse(project, out var projectId) &&
            Guid.TryParse(task, out var taskId))
        {
            activateTask(projectId, taskId);
        }
    }

    private static bool ShouldNotify(TaskRunState state) => state is
        TaskRunState.WaitingForApproval or TaskRunState.WaitingForInput or TaskRunState.Completed or
        TaskRunState.Failed or TaskRunState.Disconnected;
}
