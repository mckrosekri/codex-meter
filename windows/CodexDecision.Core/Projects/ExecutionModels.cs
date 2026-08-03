namespace CodexDecision.Core.Projects;

public enum ExecutionLocation
{
    Local,
    ManagedWorktree,
}

public enum ExecutionProfileKind
{
    NativeWindows,
    Wsl,
}

public sealed record ProjectExecutionProfile(
    ExecutionProfileKind Kind = ExecutionProfileKind.NativeWindows,
    string? WslDistribution = null,
    string? Shell = null,
    string? Editor = null,
    IReadOnlyList<string>? SetupCommands = null)
{
    public static ProjectExecutionProfile Native { get; } = new();
}

public sealed record WorktreeMetadata(
    string SourceRepository,
    string WorktreePath,
    string BaseReference,
    string BaseCommit,
    string? BranchName = null,
    DateTimeOffset? CreatedAt = null);

public sealed record GitTurnCheckpoint(
    Guid Id,
    string RepositoryRoot,
    string BeforeTree,
    string? AfterTree,
    DateTimeOffset CreatedAt,
    bool IsUndone = false,
    string? TurnId = null,
    IReadOnlyList<string>? IndividuallyUndonePaths = null);
