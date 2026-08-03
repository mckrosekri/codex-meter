using CodexDecision.Core.Routing;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Projects;

public sealed record WorkspaceState(
    int Version,
    IReadOnlyList<ProjectRecord> Projects,
    FollowUpBehavior FollowUpBehavior = FollowUpBehavior.Queue,
    IReadOnlyList<SpecialSkillId>? EnabledSpecialSkills = null,
    AutoRoutingMode AutoRoutingMode = AutoRoutingMode.Adaptive,
    SecureContinuityMode SecureContinuityMode = SecureContinuityMode.Disabled,
    RoutingPolicy RoutingPolicy = RoutingPolicy.Balanced,
    NotificationPreference NotificationPreference = NotificationPreference.BackgroundOnly)
{
    public static WorkspaceState Empty { get; } = new(2, []);
}

public enum NotificationPreference
{
    Never,
    BackgroundOnly,
    Always,
}

public sealed record ProjectRecord(
    Guid Id,
    string Name,
    IReadOnlyList<string> Folders,
    bool IsPinned,
    RoutingOverride? RoutingLock,
    IReadOnlyList<TaskRecord> Tasks,
    ProjectExecutionProfile? ExecutionProfile = null,
    bool IsDiscovered = false,
    VerificationPolicy? VerificationPolicy = null);

public sealed record TaskRecord(
    Guid Id,
    string? ThreadId,
    string Title,
    string WorkingDirectory,
    bool IsPinned,
    bool IsArchived,
    RoutingOverride? RoutingLock,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ExecutionLocation ExecutionLocation = ExecutionLocation.Local,
    WorktreeMetadata? Worktree = null,
    GitTurnCheckpoint? LastCheckpoint = null);

public sealed record NativeThreadRecord(
    string ThreadId,
    string Title,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived,
    string? ProjectDirectory = null);

public sealed record NativeProjectRecord(string Name, string Folder);

public sealed record NativeWorkspaceSnapshot(
    IReadOnlyList<NativeProjectRecord> Projects,
    IReadOnlyDictionary<string, string> ThreadProjectDirectories,
    IReadOnlyDictionary<string, string> ThreadWorkspaceRootHints)
{
    public static NativeWorkspaceSnapshot Empty { get; } = new(
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    public string ResolveProjectDirectory(string threadId, string workingDirectory)
    {
        if (ThreadProjectDirectories.TryGetValue(threadId, out var assigned))
        {
            return assigned;
        }

        var matchingProject = Projects
            .Where(project => IsWithin(workingDirectory, project.Folder))
            .OrderByDescending(project => project.Folder.Length)
            .FirstOrDefault();
        if (matchingProject is not null)
        {
            return matchingProject.Folder;
        }

        return ThreadWorkspaceRootHints.TryGetValue(threadId, out var rootHint)
            ? rootHint
            : Path.GetFullPath(workingDirectory);
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}
