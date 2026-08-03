namespace CodexDecision.Core.Projects;

public static class WorkspaceRecencyOrdering
{
    public static IReadOnlyList<ProjectRecord> OrderProjects(
        IEnumerable<ProjectRecord> projects,
        IReadOnlyDictionary<string, DateTimeOffset>? threadActivity = null,
        IReadOnlyDictionary<Guid, DateTimeOffset>? liveTaskActivity = null) =>
        projects
            .OrderByDescending(project => project.IsPinned)
            .ThenByDescending(project => GetProjectLastUsedAt(
                project,
                threadActivity,
                liveTaskActivity))
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static IReadOnlyList<TaskRecord> OrderTasks(
        IEnumerable<TaskRecord> tasks,
        IReadOnlyDictionary<string, DateTimeOffset>? threadActivity = null,
        IReadOnlyDictionary<Guid, DateTimeOffset>? liveTaskActivity = null) =>
        tasks
            .OrderByDescending(task => task.IsPinned)
            .ThenByDescending(task => GetTaskLastUsedAt(
                task,
                threadActivity,
                liveTaskActivity))
            .ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static DateTimeOffset GetProjectLastUsedAt(
        ProjectRecord project,
        IReadOnlyDictionary<string, DateTimeOffset>? threadActivity = null,
        IReadOnlyDictionary<Guid, DateTimeOffset>? liveTaskActivity = null) =>
        project.Tasks
            .Where(task => !task.IsArchived)
            .Select(task => GetTaskLastUsedAt(task, threadActivity, liveTaskActivity))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

    public static DateTimeOffset GetTaskLastUsedAt(
        TaskRecord task,
        IReadOnlyDictionary<string, DateTimeOffset>? threadActivity = null,
        IReadOnlyDictionary<Guid, DateTimeOffset>? liveTaskActivity = null)
    {
        var lastUsedAt = !string.IsNullOrWhiteSpace(task.ThreadId) &&
                         threadActivity?.TryGetValue(task.ThreadId!, out var indexedAt) is true
            ? indexedAt
            : task.UpdatedAt;

        if (liveTaskActivity?.TryGetValue(task.Id, out var liveAt) is true && liveAt > lastUsedAt)
        {
            lastUsedAt = liveAt;
        }

        return lastUsedAt;
    }
}
