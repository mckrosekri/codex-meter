using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Tests;

public sealed class WorkspaceCoordinatorTests
{
    [Fact]
    public async Task PersistsProjectTaskThreadAndIndependentLocks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-project-{Guid.NewGuid():N}");
        var statePath = Path.Combine(root, "state", "workspace.json");
        Directory.CreateDirectory(root);
        try
        {
            var coordinator = new WorkspaceCoordinator(new WorkspaceStore(statePath));
            await coordinator.InitializeAsync();
            var project = await coordinator.AddProjectAsync("Project", root);
            var task = await coordinator.CreateTaskAsync(project.Id, "New task");
            await coordinator.AttachThreadAsync(project.Id, task.Id, "thread-1");
            await coordinator.SetTaskTitleAsync(project.Id, task.Id, "Renamed task");
            await coordinator.SetProjectLockAsync(
                project.Id,
                new RoutingOverride("gpt-5.6-terra", "medium"));
            await coordinator.SetTaskLockAsync(
                project.Id,
                task.Id,
                new RoutingOverride(Permission: PermissionLevel.ReadOnly));
            await coordinator.SetFollowUpBehaviorAsync(FollowUpBehavior.Steer);
            await coordinator.SetAutoRoutingModeAsync(AutoRoutingMode.SingleModel);
            await coordinator.SetSpecialSkillEnabledAsync(SpecialSkillId.PromptMasterCodex, true);
            await coordinator.SetProjectVerificationPolicyAsync(
                project.Id,
                new VerificationPolicy(true,
                [
                    new VerificationStepDefinition(Guid.NewGuid(), "Tests", "dotnet test", 120),
                ]));

            var reloaded = new WorkspaceCoordinator(new WorkspaceStore(statePath));
            await reloaded.InitializeAsync();
            var savedProject = reloaded.GetProject(project.Id);
            var savedTask = reloaded.GetTask(project.Id, task.Id);

            Assert.Equal("gpt-5.6-terra", savedProject.RoutingLock?.ModelId);
            Assert.Equal("thread-1", savedTask.ThreadId);
            Assert.Equal("Renamed task", savedTask.Title);
            Assert.Equal(PermissionLevel.ReadOnly, savedTask.RoutingLock?.Permission);
            Assert.Equal(FollowUpBehavior.Steer, reloaded.State.FollowUpBehavior);
            Assert.Equal(AutoRoutingMode.SingleModel, reloaded.State.AutoRoutingMode);
            Assert.True(reloaded.IsSpecialSkillEnabled(SpecialSkillId.PromptMasterCodex));
            Assert.True(savedProject.VerificationPolicy?.IsEnabled);
            Assert.Equal("dotnet test", Assert.Single(savedProject.VerificationPolicy!.Steps).Command);

            await reloaded.SetSpecialSkillEnabledAsync(SpecialSkillId.PromptMasterCodex, false);
            Assert.False(reloaded.IsSpecialSkillEnabled(SpecialSkillId.PromptMasterCodex));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesExistingProjectAndThreadMappings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-project-{Guid.NewGuid():N}");
        var statePath = Path.Combine(root, "state", "workspace.json");
        Directory.CreateDirectory(root);
        try
        {
            var coordinator = new WorkspaceCoordinator(new WorkspaceStore(statePath));
            await coordinator.InitializeAsync();
            var first = await coordinator.AddProjectAsync("Project", root);
            var duplicate = await coordinator.AddProjectAsync("Renamed", root + Path.DirectorySeparatorChar);
            Assert.Equal(first.Id, duplicate.Id);

            var imported = await coordinator.ImportThreadAsync(
                first.Id,
                "thread-1",
                "Imported",
                root,
                DateTimeOffset.UtcNow);
            var duplicateThread = await coordinator.ImportThreadAsync(
                first.Id,
                "thread-1",
                "Different title",
                root,
                DateTimeOffset.UtcNow);

            Assert.Equal(imported.Id, duplicateThread.Id);
            Assert.Single(coordinator.GetProject(first.Id).Tasks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreatesQuestionTasksWithoutRequiringGitOrASelectedProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-questions-{Guid.NewGuid():N}");
        var questionFolder = Path.Combine(root, "questions");
        try
        {
            var coordinator = new WorkspaceCoordinator(
                new WorkspaceStore(Path.Combine(root, "state", "workspace.json")));
            await coordinator.InitializeAsync();

            var first = await coordinator.CreateQuestionTaskAsync(questionFolder);
            var second = await coordinator.CreateQuestionTaskAsync(questionFolder);

            Assert.Equal(first.Project.Id, second.Project.Id);
            Assert.Equal("Questions", first.Project.Name);
            Assert.Equal(Path.GetFullPath(questionFolder), first.Task.WorkingDirectory);
            Assert.Equal(ExecutionLocation.Local, first.Task.ExecutionLocation);
            Assert.Null(first.Task.Worktree);
            Assert.False(Directory.Exists(Path.Combine(questionFolder, ".git")));
            Assert.Equal(2, coordinator.GetProject(first.Project.Id).Tasks.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AutomaticallyIndexesNativeThreadsIntoProjectsInOneIdempotentPass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-index-{Guid.NewGuid():N}");
        var firstFolder = Path.Combine(root, "first-project");
        var nestedFolder = Path.Combine(firstFolder, "src");
        var secondFolder = Path.Combine(root, "second-project");
        Directory.CreateDirectory(nestedFolder);
        Directory.CreateDirectory(secondFolder);
        try
        {
            var coordinator = new WorkspaceCoordinator(
                new WorkspaceStore(Path.Combine(root, "state", "workspace.json")));
            await coordinator.InitializeAsync();
            var existing = await coordinator.AddProjectAsync("My custom name", firstFolder);
            var now = DateTimeOffset.UtcNow;
            NativeThreadRecord[] nativeThreads =
            [
                new("active-1", "Active task", nestedFolder, now.AddDays(-2), now, false),
                new("active-2", "Second task", firstFolder, now.AddDays(-1), now.AddMinutes(-1), false),
                new("archived-1", "Archived task", secondFolder, now.AddDays(-3), now.AddDays(-1), true),
            ];

            await coordinator.SynchronizeNativeThreadsAsync(nativeThreads);
            await coordinator.SynchronizeNativeThreadsAsync(nativeThreads);

            Assert.Equal(2, coordinator.State.Projects.Count);
            var firstProject = coordinator.GetProject(existing.Id);
            Assert.Equal("My custom name", firstProject.Name);
            Assert.Equal(2, firstProject.Tasks.Count);
            Assert.Contains(firstProject.Tasks, task => task.ThreadId == "active-1" &&
                                                        task.WorkingDirectory == Path.GetFullPath(nestedFolder));
            var discovered = Assert.Single(coordinator.State.Projects, project => project.Id != existing.Id);
            Assert.Equal("second-project", discovered.Name);
            var archived = Assert.Single(discovered.Tasks);
            Assert.Equal("archived-1", archived.ThreadId);
            Assert.True(archived.IsArchived);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegroupsDiscoveredChatsAndKeepsSavedProjectsWithoutTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-regroup-{Guid.NewGuid():N}");
        var projectFolder = Path.Combine(root, "project");
        var temporaryChatFolder = Path.Combine(root, "generated", "chat-1");
        var emptyProjectFolder = Path.Combine(root, "empty-project");
        Directory.CreateDirectory(projectFolder);
        Directory.CreateDirectory(temporaryChatFolder);
        try
        {
            var coordinator = new WorkspaceCoordinator(
                new WorkspaceStore(Path.Combine(root, "state", "workspace.json")));
            await coordinator.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            await coordinator.SynchronizeNativeThreadsAsync(
            [
                new NativeThreadRecord(
                    "thread-1",
                    "Task",
                    temporaryChatFolder,
                    now,
                    now,
                    false),
            ]);

            await coordinator.SynchronizeNativeThreadsAsync(
            [
                new NativeThreadRecord(
                    "thread-1",
                    "Task",
                    temporaryChatFolder,
                    now,
                    now,
                    false,
                    projectFolder),
            ],
            [
                new NativeProjectRecord("Project", projectFolder),
                new NativeProjectRecord("Empty project", emptyProjectFolder),
            ]);

            Assert.Equal(2, coordinator.State.Projects.Count);
            var project = Assert.Single(coordinator.State.Projects, candidate => candidate.Name == "Project");
            Assert.Equal("thread-1", Assert.Single(project.Tasks).ThreadId);
            Assert.Empty(Assert.Single(
                coordinator.State.Projects,
                candidate => candidate.Name == "Empty project").Tasks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsTaskWorkingDirectoryOutsideProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-project-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"codex-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var coordinator = new WorkspaceCoordinator(
                new WorkspaceStore(Path.Combine(root, "state", "workspace.json")));
            await coordinator.InitializeAsync();
            var project = await coordinator.AddProjectAsync("Project", root);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CreateTaskAsync(project.Id, "Invalid", outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
