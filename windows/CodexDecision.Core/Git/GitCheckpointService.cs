using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Git;

/// <summary>
/// Previewable tracked-file checkpoints backed by Git tree objects. Untracked files are never
/// deleted by undo/redo and remain visible for manual review.
/// </summary>
public sealed class GitCheckpointService(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new SystemProcessRunner();

    public async Task<GitTurnCheckpoint> CaptureBeforeAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = await GitOutputAsync(workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken);
        return new GitTurnCheckpoint(
            Guid.NewGuid(),
            Path.GetFullPath(root.Replace('/', Path.DirectorySeparatorChar)),
            await CaptureTreeAsync(workingDirectory, cancellationToken),
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<GitTurnCheckpoint> CompleteAsync(
        GitTurnCheckpoint checkpoint,
        string? turnId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return checkpoint with
        {
            AfterTree = await CaptureTreeAsync(checkpoint.RepositoryRoot, cancellationToken),
            TurnId = turnId,
        };
    }

    public async Task<string> PreviewAsync(
        GitTurnCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.AfterTree))
        {
            return "The turn checkpoint has not been finalized.";
        }

        var result = await RunGitAsync(
            checkpoint.RepositoryRoot,
            ["diff", "--stat", checkpoint.BeforeTree, checkpoint.AfterTree, "--"],
            cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "No tracked-file changes were recorded for this turn."
            : result.StandardOutput.Trim();
    }

    public async Task<GitTurnCheckpoint> UndoAsync(
        GitTurnCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.AfterTree))
        {
            throw new InvalidOperationException("The checkpoint is incomplete and cannot be undone.");
        }

        if (checkpoint.IsUndone)
        {
            return checkpoint;
        }

        var changedPaths = await GetChangedPathsAsync(checkpoint, cancellationToken);
        var alreadyUndone = new HashSet<string>(
            checkpoint.IndividuallyUndonePaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        var remainingPaths = changedPaths.Where(path => !alreadyUndone.Contains(path)).ToArray();

        await RequirePathsAtTreeAsync(
            checkpoint.RepositoryRoot,
            checkpoint.AfterTree,
            remainingPaths,
            "The working tree changed after this checkpoint. Review or commit the newer work before undoing the turn.",
            cancellationToken);
        await RestorePathsAsync(
            checkpoint.RepositoryRoot,
            checkpoint.BeforeTree,
            remainingPaths,
            cancellationToken);
        return checkpoint with
        {
            IsUndone = true,
            IndividuallyUndonePaths = changedPaths,
        };
    }

    public async Task<GitTurnCheckpoint> UndoFileAsync(
        GitTurnCheckpoint checkpoint,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.AfterTree))
        {
            throw new InvalidOperationException("The checkpoint is incomplete and cannot be undone.");
        }

        if (checkpoint.IsUndone)
        {
            return checkpoint;
        }

        var normalizedPath = NormalizeCheckpointPath(checkpoint.RepositoryRoot, path);
        var changedPaths = await GetChangedPathsAsync(checkpoint, cancellationToken);
        if (!changedPaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This file is not part of the tracked changes captured for the latest turn.");
        }

        var individuallyUndone = new HashSet<string>(
            checkpoint.IndividuallyUndonePaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (!individuallyUndone.Add(normalizedPath))
        {
            return checkpoint;
        }

        await RequirePathsAtTreeAsync(
            checkpoint.RepositoryRoot,
            checkpoint.AfterTree,
            [normalizedPath],
            "This file changed after the checkpoint. Review or commit the newer version before undoing it.",
            cancellationToken);
        await RestorePathsAsync(
            checkpoint.RepositoryRoot,
            checkpoint.BeforeTree,
            [normalizedPath],
            cancellationToken);

        return checkpoint with
        {
            IsUndone = individuallyUndone.Count == changedPaths.Count,
            IndividuallyUndonePaths = individuallyUndone.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    public async Task<GitTurnCheckpoint> RedoAsync(
        GitTurnCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.AfterTree))
        {
            throw new InvalidOperationException("The checkpoint is incomplete and cannot be redone.");
        }

        if (!checkpoint.IsUndone)
        {
            return checkpoint;
        }

        var changedPaths = await GetChangedPathsAsync(checkpoint, cancellationToken);
        await RequirePathsAtTreeAsync(
            checkpoint.RepositoryRoot,
            checkpoint.BeforeTree,
            changedPaths,
            "The working tree changed after this checkpoint was undone. Review or commit the newer work before redoing the turn.",
            cancellationToken);
        await RestorePathsAsync(
            checkpoint.RepositoryRoot,
            checkpoint.AfterTree,
            changedPaths,
            cancellationToken);
        return checkpoint with
        {
            IsUndone = false,
            IndividuallyUndonePaths = [],
        };
    }

    private async Task<string> CaptureTreeAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var stash = await RunGitAsync(
            workingDirectory,
            ["stash", "create", "codex-decision-checkpoint"],
            cancellationToken);
        var objectId = stash.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(objectId))
        {
            objectId = "HEAD";
        }

        return await GitOutputAsync(workingDirectory, ["rev-parse", $"{objectId}^{{tree}}"], cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetChangedPathsAsync(
        GitTurnCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            checkpoint.RepositoryRoot,
            ["diff", "--name-only", "--no-renames", checkpoint.BeforeTree, checkpoint.AfterTree!, "--"],
            cancellationToken);
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeGitPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RequirePathsAtTreeAsync(
        string workingDirectory,
        string expectedTree,
        IReadOnlyList<string> paths,
        string conflictMessage,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var arguments = new List<string> { "diff", "--quiet", expectedTree, "--" };
        arguments.AddRange(paths);
        var result = await RunGitUncheckedAsync(workingDirectory, arguments, cancellationToken);
        if (result.ExitCode == 0)
        {
            return;
        }

        if (result.ExitCode == 1)
        {
            throw new InvalidOperationException(conflictMessage);
        }

        throw new GitCommandException("Git checkpoint", result);
    }

    private async Task RestorePathsAsync(
        string workingDirectory,
        string tree,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var arguments = new List<string> { "restore", "--source", tree, "--staged", "--worktree", "--" };
        arguments.AddRange(paths);
        await RunGitAsync(
            workingDirectory,
            arguments,
            cancellationToken);
    }

    private static string NormalizeCheckpointPath(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path is required.", nameof(path));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var relative = Path.IsPathRooted(path)
            ? Path.GetRelativePath(root, Path.GetFullPath(path))
            : path;
        var normalized = NormalizeGitPath(relative);
        if (normalized is "." or ".." ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("The selected file is outside the checkpoint repository.");
        }

        return normalized;
    }

    private static string NormalizeGitPath(string path) => path.Replace('\\', '/').Trim();

    private async Task<string> GitOutputAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        (await RunGitAsync(workingDirectory, arguments, cancellationToken)).StandardOutput.Trim();

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitUncheckedAsync(workingDirectory, arguments, cancellationToken);
        return result.Succeeded ? result : throw new GitCommandException("Git checkpoint", result);
    }

    private Task<ProcessResult> RunGitUncheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = new List<string> { "-C", Path.GetFullPath(workingDirectory) };
        command.AddRange(arguments);
        return runner.RunAsync("git", command, cancellationToken);
    }
}
