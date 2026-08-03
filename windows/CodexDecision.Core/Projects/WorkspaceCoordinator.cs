using CodexDecision.Core.Routing;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Projects;

public sealed class WorkspaceCoordinator(WorkspaceStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public WorkspaceState State { get; private set; } = WorkspaceState.Empty;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            State = await store.LoadAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ProjectRecord> AddProjectAsync(
        string name,
        string folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var normalizedFolder = Path.GetFullPath(folder);
        return MutateAsync(state =>
        {
            var existing = state.Projects.FirstOrDefault(project =>
                project.Folders.Any(candidate => PathsEqual(candidate, normalizedFolder)));
            if (existing is not null)
            {
                return (state, existing);
            }

            var project = new ProjectRecord(
                Guid.NewGuid(),
                name.Trim(),
                [normalizedFolder],
                false,
                null,
                []);
            return (state with { Projects = [.. state.Projects, project] }, project);
        }, cancellationToken);
    }

    public async Task<(ProjectRecord Project, TaskRecord Task)> CreateQuestionTaskAsync(
        string workingDirectory,
        string title = "New question",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var folder = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(folder);
        var project = await AddProjectAsync("Questions", folder, cancellationToken);
        var task = await CreateTaskAsync(
            project.Id,
            title,
            folder,
            ExecutionLocation.Local,
            cancellationToken: cancellationToken);
        return (project, task);
    }

    public Task<TaskRecord> CreateTaskAsync(
        Guid projectId,
        string title,
        string? workingDirectory = null,
        ExecutionLocation executionLocation = ExecutionLocation.Local,
        WorktreeMetadata? worktree = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId);
            var cwd = Path.GetFullPath(workingDirectory ?? project.Folders[0]);
            if (executionLocation is ExecutionLocation.Local &&
                !project.Folders.Any(folder => IsWithin(cwd, folder)))
            {
                throw new InvalidOperationException("A task working directory must stay inside its project folders.");
            }

            if (executionLocation is ExecutionLocation.ManagedWorktree &&
                (worktree is null || !PathsEqual(cwd, worktree.WorktreePath) ||
                 !project.Folders.Any(folder => IsWithin(worktree.SourceRepository, folder))))
            {
                throw new InvalidOperationException("A managed worktree task requires matching, project-scoped worktree metadata.");
            }

            var now = DateTimeOffset.UtcNow;
            var task = new TaskRecord(
                Guid.NewGuid(),
                null,
                title.Trim(),
                cwd,
                false,
                false,
                null,
                now,
                now,
                executionLocation,
                worktree);
            var updated = project with { Tasks = [task, .. project.Tasks] };
            return (ReplaceProject(state, updated), task);
        }, cancellationToken);
    }

    public Task<TaskRecord> ImportThreadAsync(
        Guid projectId,
        string threadId,
        string title,
        string workingDirectory,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId);
            var existing = project.Tasks.FirstOrDefault(task =>
                string.Equals(task.ThreadId, threadId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return (state, existing);
            }

            var task = new TaskRecord(
                Guid.NewGuid(),
                threadId,
                string.IsNullOrWhiteSpace(title) ? "Untitled task" : title.Trim(),
                Path.GetFullPath(workingDirectory),
                false,
                false,
                null,
                updatedAt,
                updatedAt);
            var updated = project with { Tasks = [task, .. project.Tasks] };
            return (ReplaceProject(state, updated), task);
        }, cancellationToken);
    }

    public Task<WorkspaceState> SynchronizeNativeThreadsAsync(
        IReadOnlyList<NativeThreadRecord> threads,
        IReadOnlyList<NativeProjectRecord>? nativeProjects = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        nativeProjects ??= [];
        return MutateAsync(state =>
        {
            var projects = state.Projects.ToList();
            var changed = false;
            var nativeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var nativeProject in nativeProjects)
            {
                if (string.IsNullOrWhiteSpace(nativeProject.Folder))
                {
                    continue;
                }

                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nativeProject.Folder));
                nativeFolders.Add(folder);
                if (FindExactProject(projects, folder) >= 0)
                {
                    continue;
                }

                projects.Add(new ProjectRecord(
                    Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(nativeProject.Name) ? ProjectName(folder) : nativeProject.Name.Trim(),
                    [folder],
                    false,
                    null,
                    [],
                    IsDiscovered: true));
                changed = true;
            }

            var threadLocations = projects
                .SelectMany((project, projectIndex) => project.Tasks
                    .Where(task => !string.IsNullOrWhiteSpace(task.ThreadId))
                    .Select(task => (task.ThreadId!, ProjectIndex: projectIndex, TaskId: task.Id)))
                .GroupBy(item => item.Item1, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (group.First().ProjectIndex, group.First().TaskId),
                    StringComparer.Ordinal);
            var cleanupCandidates = new HashSet<Guid>();

            foreach (var native in threads
                         .Where(thread => !string.IsNullOrWhiteSpace(thread.ThreadId) &&
                                          !string.IsNullOrWhiteSpace(thread.WorkingDirectory))
                         .GroupBy(thread => thread.ThreadId, StringComparer.Ordinal)
                         .Select(group => group.OrderBy(thread => thread.IsArchived).First()))
            {
                var workingDirectory = Path.GetFullPath(native.WorkingDirectory);
                var projectDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(native.ProjectDirectory ?? workingDirectory));
                var title = string.IsNullOrWhiteSpace(native.Title) ? "Untitled task" : native.Title.Trim();
                var targetProjectIndex = FindExactProject(projects, projectDirectory);
                if (targetProjectIndex < 0 && native.ProjectDirectory is null)
                {
                    targetProjectIndex = FindContainingProject(projects, projectDirectory);
                }

                if (targetProjectIndex < 0)
                {
                    projects.Add(new ProjectRecord(
                        Guid.NewGuid(),
                        ProjectName(projectDirectory),
                        [projectDirectory],
                        false,
                        null,
                        [],
                        IsDiscovered: true));
                    targetProjectIndex = projects.Count - 1;
                    changed = true;
                }

                if (threadLocations.TryGetValue(native.ThreadId, out var existingLocation))
                {
                    var sourceProject = projects[existingLocation.ProjectIndex];
                    var existing = sourceProject.Tasks.First(task => task.Id == existingLocation.TaskId);
                    var updated = existing with
                    {
                        Title = title,
                        WorkingDirectory = existing.ExecutionLocation is ExecutionLocation.Local
                            ? workingDirectory
                            : existing.WorkingDirectory,
                        IsArchived = existing.IsArchived || native.IsArchived,
                        CreatedAt = native.CreatedAt < existing.CreatedAt ? native.CreatedAt : existing.CreatedAt,
                        UpdatedAt = native.UpdatedAt > existing.UpdatedAt ? native.UpdatedAt : existing.UpdatedAt,
                    };
                    var canMove = existingLocation.ProjectIndex != targetProjectIndex &&
                                  LooksAutomaticallyDiscovered(sourceProject) &&
                                  !nativeFolders.Contains(Path.TrimEndingDirectorySeparator(
                                      Path.GetFullPath(sourceProject.Folders[0])));
                    if (canMove)
                    {
                        projects[existingLocation.ProjectIndex] = sourceProject with
                        {
                            Tasks = sourceProject.Tasks.Where(task => task.Id != updated.Id).ToArray(),
                        };
                        var moveTarget = projects[targetProjectIndex];
                        projects[targetProjectIndex] = moveTarget with
                        {
                            Tasks = [updated, .. moveTarget.Tasks],
                        };
                        cleanupCandidates.Add(sourceProject.Id);
                        changed = true;
                    }
                    else if (updated != existing)
                    {
                        projects[existingLocation.ProjectIndex] = sourceProject with
                        {
                            Tasks = sourceProject.Tasks.Select(task => task.Id == updated.Id ? updated : task).ToArray(),
                        };
                        changed = true;
                    }

                    continue;
                }

                var targetProject = projects[targetProjectIndex];
                var imported = new TaskRecord(
                    Guid.NewGuid(),
                    native.ThreadId,
                    title,
                    workingDirectory,
                    false,
                    native.IsArchived,
                    null,
                    native.CreatedAt,
                    native.UpdatedAt);
                projects[targetProjectIndex] = targetProject with { Tasks = [imported, .. targetProject.Tasks] };
                threadLocations[native.ThreadId] = (targetProjectIndex, imported.Id);
                changed = true;
            }

            if (cleanupCandidates.Count > 0)
            {
                projects = projects.Where(project =>
                    project.Tasks.Count > 0 || !cleanupCandidates.Contains(project.Id)).ToList();
            }

            var next = changed ? state with { Projects = projects.ToArray() } : state;
            return (next, next);
        }, cancellationToken);
    }

    public Task<TaskRecord> AttachThreadAsync(
        Guid projectId,
        Guid taskId,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return UpdateTaskAsync(
            projectId,
            taskId,
            task => task with { ThreadId = threadId, UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken);
    }

    public Task<TaskRecord> SetTaskTitleAsync(
        Guid projectId,
        Guid taskId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return UpdateTaskAsync(
            projectId,
            taskId,
            task => task with { Title = title.Trim(), UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken);
    }

    public Task<ProjectRecord> SetProjectLockAsync(
        Guid projectId,
        RoutingOverride? routingLock,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId) with { RoutingLock = NormalizeLock(routingLock) };
            return (ReplaceProject(state, project), project);
        }, cancellationToken);
    }

    public Task<ProjectRecord> RenameProjectAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId) with { Name = name.Trim() };
            return (ReplaceProject(state, project), project);
        }, cancellationToken);
    }

    public Task<ProjectRecord> SetProjectPinnedAsync(
        Guid projectId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId) with { IsPinned = pinned };
            return (ReplaceProject(state, project), project);
        }, cancellationToken);
    }

    public Task<ProjectRecord> SetProjectExecutionProfileAsync(
        Guid projectId,
        ProjectExecutionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind is ExecutionProfileKind.Wsl && string.IsNullOrWhiteSpace(profile.WslDistribution))
        {
            throw new ArgumentException("A WSL execution profile requires a distribution name.", nameof(profile));
        }

        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId) with { ExecutionProfile = profile };
            return (ReplaceProject(state, project), project);
        }, cancellationToken);
    }

    public Task<ProjectRecord> SetProjectVerificationPolicyAsync(
        Guid projectId,
        VerificationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = policy.Normalize();
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId) with { VerificationPolicy = normalized };
            return (ReplaceProject(state, project), project);
        }, cancellationToken);
    }

    public Task<TaskRecord> SetTaskPinnedAsync(
        Guid projectId,
        Guid taskId,
        bool pinned,
        CancellationToken cancellationToken = default) =>
        UpdateTaskAsync(projectId, taskId, task => task with
        {
            IsPinned = pinned,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

    public Task<TaskRecord> SetTaskArchivedAsync(
        Guid projectId,
        Guid taskId,
        bool archived,
        CancellationToken cancellationToken = default) =>
        UpdateTaskAsync(projectId, taskId, task => task with
        {
            IsArchived = archived,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

    public Task<TaskRecord> SetTaskCheckpointAsync(
        Guid projectId,
        Guid taskId,
        GitTurnCheckpoint? checkpoint,
        CancellationToken cancellationToken = default) =>
        UpdateTaskAsync(projectId, taskId, task => task with
        {
            LastCheckpoint = checkpoint,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

    public Task<TaskRecord> SetTaskLockAsync(
        Guid projectId,
        Guid taskId,
        RoutingOverride? routingLock,
        CancellationToken cancellationToken = default)
    {
        return UpdateTaskAsync(
            projectId,
            taskId,
            task => task with { RoutingLock = NormalizeLock(routingLock), UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken);
    }

    public Task<FollowUpBehavior> SetFollowUpBehaviorAsync(
        FollowUpBehavior behavior,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var next = state.FollowUpBehavior == behavior
                ? state
                : state with { FollowUpBehavior = behavior };
            return (next, behavior);
        }, cancellationToken);
    }

    public Task<AutoRoutingMode> SetAutoRoutingModeAsync(
        AutoRoutingMode mode,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var next = state.AutoRoutingMode == mode
                ? state
                : state with { AutoRoutingMode = mode };
            return (next, mode);
        }, cancellationToken);
    }

    public Task<SecureContinuityMode> SetSecureContinuityModeAsync(
        SecureContinuityMode mode,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var next = state.SecureContinuityMode == mode
                ? state
                : state with { SecureContinuityMode = mode };
            return (next, mode);
        }, cancellationToken);
    }

    public Task<RoutingPolicy> SetRoutingPolicyAsync(
        RoutingPolicy policy,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var next = state.RoutingPolicy == policy
                ? state
                : state with { RoutingPolicy = policy };
            return (next, policy);
        }, cancellationToken);
    }

    public Task<NotificationPreference> SetNotificationPreferenceAsync(
        NotificationPreference preference,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            var next = state.NotificationPreference == preference
                ? state
                : state with { NotificationPreference = preference };
            return (next, preference);
        }, cancellationToken);
    }

    public Task<bool> SetSpecialSkillEnabledAsync(
        SpecialSkillId skill,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        _ = SpecialSkillCatalog.Get(skill);
        return MutateAsync(state =>
        {
            var current = SpecialSkillCatalog.Normalize(state.EnabledSpecialSkills);
            var nextSkills = enabled
                ? SpecialSkillCatalog.Normalize([.. current, skill])
                : current.Where(candidate => candidate != skill).ToArray();
            var changed = !current.SequenceEqual(nextSkills);
            var next = changed ? state with { EnabledSpecialSkills = nextSkills } : state;
            return (next, enabled);
        }, cancellationToken);
    }

    public bool IsSpecialSkillEnabled(SpecialSkillId skill)
    {
        return SpecialSkillCatalog.Normalize(State.EnabledSpecialSkills).Contains(skill);
    }

    public ProjectRecord GetProject(Guid projectId) => RequireProject(State, projectId);

    public TaskRecord GetTask(Guid projectId, Guid taskId)
    {
        return RequireProject(State, projectId).Tasks.FirstOrDefault(task => task.Id == taskId)
               ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    private Task<TaskRecord> UpdateTaskAsync(
        Guid projectId,
        Guid taskId,
        Func<TaskRecord, TaskRecord> update,
        CancellationToken cancellationToken)
    {
        return MutateAsync(state =>
        {
            var project = RequireProject(state, projectId);
            var task = project.Tasks.FirstOrDefault(candidate => candidate.Id == taskId)
                       ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
            var updatedTask = update(task);
            var updatedProject = project with
            {
                Tasks = project.Tasks.Select(candidate =>
                    candidate.Id == taskId ? updatedTask : candidate).ToArray(),
            };
            return (ReplaceProject(state, updatedProject), updatedTask);
        }, cancellationToken);
    }

    private async Task<T> MutateAsync<T>(
        Func<WorkspaceState, (WorkspaceState State, T Result)> mutation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (next, result) = mutation(State);
            if (!ReferenceEquals(next, State))
            {
                await store.SaveAsync(next, cancellationToken);
                State = next;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static ProjectRecord RequireProject(WorkspaceState state, Guid projectId)
    {
        return state.Projects.FirstOrDefault(project => project.Id == projectId)
               ?? throw new KeyNotFoundException($"Project '{projectId}' was not found.");
    }

    private static WorkspaceState ReplaceProject(WorkspaceState state, ProjectRecord updated)
    {
        return state with
        {
            Projects = state.Projects.Select(project =>
                project.Id == updated.Id ? updated : project).ToArray(),
        };
    }

    private static RoutingOverride? NormalizeLock(RoutingOverride? value)
    {
        return value is null || value.IsEmpty ? null : value;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindContainingProject(IReadOnlyList<ProjectRecord> projects, string workingDirectory)
    {
        var bestIndex = -1;
        var bestRootLength = -1;
        for (var index = 0; index < projects.Count; index++)
        {
            foreach (var folder in projects[index].Folders)
            {
                if (!IsWithin(workingDirectory, folder))
                {
                    continue;
                }

                var length = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).Length;
                if (length > bestRootLength)
                {
                    bestIndex = index;
                    bestRootLength = length;
                }
            }
        }

        return bestIndex;
    }

    private static int FindExactProject(IReadOnlyList<ProjectRecord> projects, string folder)
    {
        for (var index = 0; index < projects.Count; index++)
        {
            if (projects[index].Folders.Any(candidate => PathsEqual(candidate, folder)))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ProjectName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return string.IsNullOrWhiteSpace(name) ? "Codex project" : name;
    }

    private static bool LooksAutomaticallyDiscovered(ProjectRecord project)
    {
        if (project.IsDiscovered)
        {
            return true;
        }

        return project.Folders.Count == 1 &&
               !project.IsPinned &&
               project.RoutingLock is null &&
               project.ExecutionProfile is null &&
               project.Tasks.Count > 0 &&
               project.Tasks.All(task => !string.IsNullOrWhiteSpace(task.ThreadId)) &&
               string.Equals(project.Name, ProjectName(project.Folders[0]), StringComparison.OrdinalIgnoreCase);
    }
}
