using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Tests;

public sealed class WorkspaceRecencyOrderingTests
{
    [Fact]
    public void OrdersChatsAndProjectsByNewestActivity()
    {
        var oldTask = Task("old", "thread-old", At(1));
        var newTask = Task("new", "thread-new", At(2));
        var oldProject = Project("Old project", oldTask);
        var newProject = Project("New project", newTask);
        var threadActivity = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["thread-old"] = At(3),
            ["thread-new"] = At(5),
        };

        var tasks = WorkspaceRecencyOrdering.OrderTasks(
            [oldTask, newTask],
            threadActivity);
        var projects = WorkspaceRecencyOrdering.OrderProjects(
            [oldProject, newProject],
            threadActivity);

        Assert.Equal(newTask.Id, tasks[0].Id);
        Assert.Equal(newProject.Id, projects[0].Id);
    }

    [Fact]
    public void LiveRunMovesItsChatAndProjectToTheTop()
    {
        var previouslyNewest = Task("previous", "thread-previous", At(5));
        var nowActive = Task("active", "thread-active", At(1));
        var previousProject = Project("Previous", previouslyNewest);
        var activeProject = Project("Active", nowActive);
        var liveActivity = new Dictionary<Guid, DateTimeOffset>
        {
            [nowActive.Id] = At(6),
        };

        var tasks = WorkspaceRecencyOrdering.OrderTasks(
            [previouslyNewest, nowActive],
            liveTaskActivity: liveActivity);
        var projects = WorkspaceRecencyOrdering.OrderProjects(
            [previousProject, activeProject],
            liveTaskActivity: liveActivity);

        Assert.Equal(nowActive.Id, tasks[0].Id);
        Assert.Equal(activeProject.Id, projects[0].Id);
    }

    [Fact]
    public void PinsStayAheadOfRecencyWithinTheirLevel()
    {
        var recent = Task("recent", "thread-recent", At(5));
        var pinned = Task("pinned", "thread-pinned", At(1)) with { IsPinned = true };
        var recentProject = Project("Recent", recent);
        var pinnedProject = Project("Pinned", pinned) with { IsPinned = true };

        var tasks = WorkspaceRecencyOrdering.OrderTasks([recent, pinned]);
        var projects = WorkspaceRecencyOrdering.OrderProjects([recentProject, pinnedProject]);

        Assert.Equal(pinned.Id, tasks[0].Id);
        Assert.Equal(pinnedProject.Id, projects[0].Id);
    }

    [Fact]
    public void UsesConversationActivityInsteadOfMetadataUpdateTime()
    {
        var metadataEdited = Task("metadata", "thread-metadata", At(6));
        var recentlyUsed = Task("recent", "thread-recent", At(2));
        var threadActivity = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["thread-metadata"] = At(1),
            ["thread-recent"] = At(5),
        };

        var tasks = WorkspaceRecencyOrdering.OrderTasks(
            [metadataEdited, recentlyUsed],
            threadActivity);

        Assert.Equal(recentlyUsed.Id, tasks[0].Id);
    }

    [Fact]
    public void ArchivedChatsDoNotLiftAProjectInTheSidebar()
    {
        var archived = Task("archived", "thread-archived", At(6)) with { IsArchived = true };
        var active = Task("active", "thread-active", At(3));
        var archivedProject = Project("Archived only", archived);
        var activeProject = Project("Active", active);

        var projects = WorkspaceRecencyOrdering.OrderProjects([archivedProject, activeProject]);

        Assert.Equal(activeProject.Id, projects[0].Id);
    }

    private static DateTimeOffset At(int hour) =>
        new(2026, 7, 17, hour, 0, 0, TimeSpan.Zero);

    private static ProjectRecord Project(string name, params TaskRecord[] tasks) => new(
        Guid.NewGuid(),
        name,
        [Path.Combine("C:\\projects", name)],
        false,
        null,
        tasks);

    private static TaskRecord Task(string title, string threadId, DateTimeOffset updatedAt) => new(
        Guid.NewGuid(),
        threadId,
        title,
        Path.Combine("C:\\projects", title),
        false,
        false,
        null,
        updatedAt.AddHours(-1),
        updatedAt);
}
