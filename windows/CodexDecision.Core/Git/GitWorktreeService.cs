using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Git;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Unable to start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

public sealed record GitRepositoryStatus(
    string RepositoryRoot,
    string HeadCommit,
    string? BranchName,
    IReadOnlyList<string> Changes)
{
    public bool IsClean => Changes.Count == 0;
}

public sealed class GitReviewFile
{
    private readonly string? stageLabelOverride;

    public GitReviewFile(
        string path,
        string statusCode,
        string statusLabel,
        bool isStaged,
        bool isUnstaged,
        bool isUntracked,
        int? additions,
        int? deletions,
        string? stageLabelOverride = null)
    {
        Path = path;
        StatusCode = statusCode;
        StatusLabel = statusLabel;
        IsStaged = isStaged;
        IsUnstaged = isUnstaged;
        IsUntracked = isUntracked;
        Additions = additions;
        Deletions = deletions;
        this.stageLabelOverride = stageLabelOverride;
    }

    public string Path { get; set; }

    public string StatusCode { get; set; }

    public string StatusLabel { get; set; }

    public bool IsStaged { get; set; }

    public bool IsUnstaged { get; set; }

    public bool IsUntracked { get; set; }

    public int? Additions { get; set; }

    public int? Deletions { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Directory
    {
        get
        {
            var directory = System.IO.Path.GetDirectoryName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(directory)
                ? "Repository root"
                : directory.Replace('\\', '/');
        }
    }

    public string StatusGlyph => IsUntracked ? "?" : StatusLabel[..1].ToUpperInvariant();

    public string StageLabel => stageLabelOverride ?? (IsUntracked
        ? "Untracked"
        : IsStaged && IsUnstaged
            ? "Staged + unstaged"
            : IsStaged
                ? "Staged"
                : "Unstaged");

    public string AdditionText => Additions is null ? string.Empty : $"+{Additions}";

    public string DeletionText => Deletions is null ? string.Empty : $"-{Deletions}";

    public string AutomationName => $"{Path}, {StatusLabel}, {StageLabel}, {AdditionText} {DeletionText}".Trim();
}

public sealed record GitReviewSnapshot(
    string RepositoryRoot,
    string HeadCommit,
    string? BranchName,
    IReadOnlyList<GitReviewFile> Files)
{
    public int TotalAdditions => Files.Sum(file => file.Additions ?? 0);

    public int TotalDeletions => Files.Sum(file => file.Deletions ?? 0);
}

public sealed record GitEnvironmentSnapshot(
    string RepositoryRoot,
    string HeadCommit,
    string? BranchName,
    string? UpstreamBranch,
    int AheadBy,
    int BehindBy,
    IReadOnlyList<GitReviewFile> Files)
{
    public bool IsClean => Files.Count == 0;

    public int TotalAdditions => Files.Sum(file => file.Additions ?? 0);

    public int TotalDeletions => Files.Sum(file => file.Deletions ?? 0);
}

public sealed class GitCommandException(string operation, ProcessResult result)
    : InvalidOperationException($"{operation} failed: {Clean(result.StandardError, result.StandardOutput)}")
{
    public int ExitCode { get; } = result.ExitCode;

    private static string Clean(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? "Git returned no diagnostic.";
}

public sealed class GitWorktreeService
{
    private readonly IProcessRunner runner;

    public GitWorktreeService(IProcessRunner? runner = null, string? managedRoot = null)
    {
        this.runner = runner ?? new SystemProcessRunner();
        ManagedRoot = Path.GetFullPath(managedRoot ?? DefaultManagedRoot());
    }

    public string ManagedRoot { get; }

    public async Task<GitRepositoryStatus> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var root = await GitAsync(["-C", fullPath, "rev-parse", "--show-toplevel"], "Locate repository", cancellationToken);
        var repositoryRoot = NormalizeGitPath(root.StandardOutput.Trim());
        var head = await GitAsync(["-C", repositoryRoot, "rev-parse", "HEAD"], "Read HEAD", cancellationToken);
        var branchResult = await runner.RunAsync(
            "git", ["-C", repositoryRoot, "symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var status = await GitAsync(
            ["-C", repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all"],
            "Read repository status",
            cancellationToken);
        return new GitRepositoryStatus(
            repositoryRoot,
            head.StandardOutput.Trim(),
            branchResult.Succeeded ? branchResult.StandardOutput.Trim() : null,
            SplitLines(status.StandardOutput));
    }

    public async Task<WorktreeMetadata> CreateManagedAsync(
        string sourcePath,
        Guid taskId,
        string baseReference = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        var source = await InspectAsync(sourcePath, cancellationToken);
        if (!source.IsClean)
        {
            throw new InvalidOperationException(
                "Managed worktrees require a clean repository. Commit or stash local changes first.");
        }

        var repositoryKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source.RepositoryRoot.ToUpperInvariant())))[..16]
            .ToLowerInvariant();
        var destination = Path.GetFullPath(Path.Combine(ManagedRoot, repositoryKey, taskId.ToString("N")));
        EnsureWithinManagedRoot(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException("The managed worktree directory already exists and is not empty.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await GitAsync(
            ["-C", source.RepositoryRoot, "worktree", "add", "--detach", destination, baseReference],
            "Create managed worktree",
            cancellationToken);
        var created = await InspectAsync(destination, cancellationToken);
        return new WorktreeMetadata(
            source.RepositoryRoot,
            destination,
            baseReference,
            created.HeadCommit,
            created.BranchName,
            DateTimeOffset.UtcNow);
    }

    public async Task<GitReviewSnapshot> GetReviewAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(workingDirectory, cancellationToken);
        var unstaged = await GitAsync(
            ["-C", status.RepositoryRoot, "diff", "--numstat", "--no-renames", "--"],
            "Read unstaged change statistics",
            cancellationToken);
        var staged = await GitAsync(
            ["-C", status.RepositoryRoot, "diff", "--cached", "--numstat", "--no-renames", "--"],
            "Read staged change statistics",
            cancellationToken);
        var statistics = ParseNumstat(unstaged.StandardOutput, staged.StandardOutput);
        var files = status.Changes
            .Select(line => ParseReviewFile(line, statistics))
            .Where(file => file is not null)
            .Cast<GitReviewFile>()
            .ToArray();
        return new GitReviewSnapshot(
            status.RepositoryRoot,
            status.HeadCommit,
            status.BranchName,
            files);
    }

    public async Task<GitEnvironmentSnapshot> GetEnvironmentAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var review = await GetReviewAsync(workingDirectory, cancellationToken);
        var upstreamResult = await runner.RunAsync(
            "git",
            ["-C", review.RepositoryRoot, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            cancellationToken);
        var upstream = upstreamResult.Succeeded
            ? upstreamResult.StandardOutput.Trim()
            : null;
        var ahead = 0;
        var behind = 0;
        if (!string.IsNullOrWhiteSpace(upstream))
        {
            var divergence = await GitAsync(
                ["-C", review.RepositoryRoot, "rev-list", "--left-right", "--count", "HEAD...@{upstream}"],
                "Read branch divergence",
                cancellationToken);
            (ahead, behind) = ParseDivergence(divergence.StandardOutput);
        }

        return new GitEnvironmentSnapshot(
            review.RepositoryRoot,
            review.HeadCommit,
            review.BranchName,
            upstream,
            ahead,
            behind,
            review.Files);
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(workingDirectory, cancellationToken);
        return await GetBranchesFromRootAsync(status.RepositoryRoot, cancellationToken);
    }

    public async Task<GitReviewSnapshot> GetBranchReviewAsync(
        string workingDirectory,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(workingDirectory, cancellationToken);
        await EnsureComparisonBaseAsync(status.RepositoryRoot, baseBranch, cancellationToken);
        var nameStatus = await GitAsync(
            ["-C", status.RepositoryRoot, "diff", "--name-status", "--no-renames", $"{baseBranch}...HEAD", "--"],
            "Read branch comparison",
            cancellationToken);
        var numstat = await GitAsync(
            ["-C", status.RepositoryRoot, "diff", "--numstat", "--no-renames", $"{baseBranch}...HEAD", "--"],
            "Read branch comparison statistics",
            cancellationToken);
        var statistics = ParseNumstat(numstat.StandardOutput);
        var files = SplitLines(nameStatus.StandardOutput)
            .Select(line => ParseComparisonFile(line, statistics))
            .Where(file => file is not null)
            .Cast<GitReviewFile>()
            .ToArray();
        return new GitReviewSnapshot(
            status.RepositoryRoot,
            status.HeadCommit,
            status.BranchName,
            files);
    }

    public async Task<string> GetFileDiffAsync(
        string repositoryRoot,
        string relativePath,
        bool isUntracked,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var safeRelative = ValidateRelativePath(root, relativePath);
        IReadOnlyList<string> arguments = isUntracked
            ? ["-C", root, "diff", "--no-index", "--", "NUL", safeRelative]
            : ["-C", root, "diff", "HEAD", "--", safeRelative];
        var result = await runner.RunAsync("git", arguments, cancellationToken);
        if (!result.Succeeded && !(isUntracked && result.ExitCode == 1))
        {
            throw new GitCommandException("Read file diff", result);
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "No textual diff is available for this file."
            : result.StandardOutput;
    }

    public async Task<string> GetBranchFileDiffAsync(
        string repositoryRoot,
        string baseBranch,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        await EnsureComparisonBaseAsync(root, baseBranch, cancellationToken);
        var safeRelative = ValidateRelativePath(root, relativePath);
        var result = await GitAsync(
            ["-C", root, "diff", "--no-ext-diff", $"{baseBranch}...HEAD", "--", safeRelative],
            "Read branch file diff",
            cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "No textual diff is available for this file."
            : result.StandardOutput;
    }

    public Task StageAsync(
        WorktreeMetadata metadata,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        RunPathCommandAsync(metadata, ["add", "--"], relativePath, "Stage file", cancellationToken);

    public Task UnstageAsync(
        WorktreeMetadata metadata,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        RunPathCommandAsync(metadata, ["restore", "--staged", "--"], relativePath, "Unstage file", cancellationToken);

    public Task DiscardAsync(
        WorktreeMetadata metadata,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        RunPathCommandAsync(metadata, ["restore", "--worktree", "--"], relativePath, "Discard file changes", cancellationToken);

    public async Task<WorktreeMetadata> CreateBranchAsync(
        WorktreeMetadata metadata,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        await CreateBranchAsync(metadata.WorktreePath, branchName, cancellationToken);
        return metadata with { BranchName = branchName };
    }

    public async Task<string> CreateBranchAsync(
        string workingDirectory,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        ValidateBranchName(branchName);
        var status = await InspectAsync(workingDirectory, cancellationToken);
        var normalized = branchName.Trim();
        await GitAsync(
            ["-C", status.RepositoryRoot, "switch", "-c", normalized],
            "Create branch",
            cancellationToken);
        return normalized;
    }

    public async Task CommitAsync(
        WorktreeMetadata metadata,
        string message,
        CancellationToken cancellationToken = default)
    {
        await CommitAsync(
            metadata.WorktreePath,
            message,
            stageAll: false,
            cancellationToken: cancellationToken);
    }

    public async Task CommitAsync(
        string workingDirectory,
        string message,
        bool stageAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var status = await InspectAsync(workingDirectory, cancellationToken);
        if (stageAll)
        {
            await GitAsync(
                ["-C", status.RepositoryRoot, "add", "-A", "--"],
                "Stage repository changes",
                cancellationToken);
        }

        await GitAsync(
            ["-C", status.RepositoryRoot, "commit", "-m", message.Trim()],
            "Commit changes",
            cancellationToken);
    }

    public async Task PushAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(status.BranchName))
        {
            throw new InvalidOperationException("Create a named branch before pushing this checkout.");
        }

        var upstream = await runner.RunAsync(
            "git",
            ["-C", status.RepositoryRoot, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            cancellationToken);
        var arguments = upstream.Succeeded
            ? new[] { "-C", status.RepositoryRoot, "push" }
            : new[] { "-C", status.RepositoryRoot, "push", "--set-upstream", "origin", status.BranchName };
        await GitAsync(arguments, "Push branch", cancellationToken);
    }

    public async Task HandoffToLocalAsync(
        WorktreeMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metadata.BranchName))
        {
            throw new InvalidOperationException("Create and commit a named branch before handing this task to Local.");
        }

        var local = await InspectAsync(metadata.SourceRepository, cancellationToken);
        var worktree = await InspectAsync(metadata.WorktreePath, cancellationToken);
        if (!local.IsClean || !worktree.IsClean)
        {
            throw new InvalidOperationException("Both Local and Worktree must be clean before handoff.");
        }

        await GitAsync(
            ["-C", metadata.WorktreePath, "switch", "--detach"],
            "Detach worktree branch",
            cancellationToken);
        await GitAsync(
            ["-C", metadata.SourceRepository, "switch", metadata.BranchName],
            "Switch Local to worktree branch",
            cancellationToken);
    }

    public async Task RemoveAsync(
        WorktreeMetadata metadata,
        bool discardChanges,
        CancellationToken cancellationToken = default)
    {
        EnsureWithinManagedRoot(metadata.WorktreePath);
        var status = await InspectAsync(metadata.WorktreePath, cancellationToken);
        if (!status.IsClean && !discardChanges)
        {
            throw new InvalidOperationException("The worktree has uncommitted changes and cannot be removed safely.");
        }

        var arguments = new List<string>
        {
            "-C", metadata.SourceRepository, "worktree", "remove",
        };
        if (discardChanges)
        {
            arguments.Add("--force");
        }

        arguments.Add(metadata.WorktreePath);
        await GitAsync(arguments, "Remove managed worktree", cancellationToken);
    }

    private async Task RunPathCommandAsync(
        WorktreeMetadata metadata,
        IReadOnlyList<string> command,
        string relativePath,
        string operation,
        CancellationToken cancellationToken)
    {
        var safeRelative = ValidateRelativePath(metadata.WorktreePath, relativePath);
        var arguments = new List<string> { "-C", metadata.WorktreePath };
        arguments.AddRange(command);
        arguments.Add(safeRelative);
        await GitAsync(arguments, operation, cancellationToken);
    }

    private static GitReviewFile? ParseReviewFile(
        string statusLine,
        IReadOnlyDictionary<string, (int? Additions, int? Deletions)> statistics)
    {
        if (statusLine.Length < 3)
        {
            return null;
        }

        var code = statusLine[..2];
        var path = StatusPath(statusLine);
        statistics.TryGetValue(NormalizeRelativeGitPath(path), out var totals);
        var isUntracked = code == "??";
        var isStaged = !isUntracked && code[0] != ' ';
        var isUnstaged = isUntracked || code[1] != ' ';
        return new GitReviewFile(
            path,
            code,
            StatusLabel(code),
            isStaged,
            isUnstaged,
            isUntracked,
            isUntracked ? null : totals.Additions,
            isUntracked ? null : totals.Deletions);
    }

    private static GitReviewFile? ParseComparisonFile(
        string nameStatusLine,
        IReadOnlyDictionary<string, (int? Additions, int? Deletions)> statistics)
    {
        var fields = nameStatusLine.Split('\t', 2);
        if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[1]))
        {
            return null;
        }

        var path = fields[1].Trim('"');
        var code = $"{fields[0][0]} ";
        statistics.TryGetValue(NormalizeRelativeGitPath(path), out var totals);
        return new GitReviewFile(
            path,
            code,
            StatusLabel(code),
            false,
            false,
            false,
            totals.Additions,
            totals.Deletions,
            "Compared");
    }

    private static string StatusLabel(string code)
    {
        if (code == "??")
        {
            return "Untracked";
        }

        if (code.Contains('U') || code is "AA" or "DD")
        {
            return "Conflict";
        }

        if (code.Contains('R'))
        {
            return "Renamed";
        }

        if (code.Contains('D'))
        {
            return "Deleted";
        }

        if (code.Contains('A'))
        {
            return "Added";
        }

        if (code.Contains('T'))
        {
            return "Type changed";
        }

        return "Modified";
    }

    private static string StatusPath(string statusLine)
    {
        var path = statusLine.Length > 3 ? statusLine[3..].Trim() : statusLine.Trim();
        var renameMarker = path.LastIndexOf(" -> ", StringComparison.Ordinal);
        return (renameMarker >= 0 ? path[(renameMarker + 4)..] : path).Trim('"');
    }

    private static IReadOnlyDictionary<string, (int? Additions, int? Deletions)> ParseNumstat(
        params string[] outputs)
    {
        var statistics = new Dictionary<string, (int? Additions, int? Deletions)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in outputs.SelectMany(SplitLines))
        {
            var fields = line.Split('\t', 3);
            if (fields.Length != 3)
            {
                continue;
            }

            var path = NormalizeRelativeGitPath(fields[2]);
            int? additions = int.TryParse(fields[0], out var parsedAdditions) ? parsedAdditions : null;
            int? deletions = int.TryParse(fields[1], out var parsedDeletions) ? parsedDeletions : null;
            if (statistics.TryGetValue(path, out var existing))
            {
                statistics[path] = (
                    existing.Additions is null || additions is null ? null : existing.Additions + additions,
                    existing.Deletions is null || deletions is null ? null : existing.Deletions + deletions);
            }
            else
            {
                statistics[path] = (additions, deletions);
            }
        }

        return statistics;
    }

    private async Task<IReadOnlyList<string>> GetBranchesFromRootAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["-C", repositoryRoot, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"],
            "List branches",
            cancellationToken);
        return SplitLines(result.StandardOutput)
            .Select(line => line.Trim())
            .Where(line => !line.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task EnsureComparisonBaseAsync(
        string repositoryRoot,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        var branches = await GetBranchesFromRootAsync(repositoryRoot, cancellationToken);
        if (!branches.Contains(baseBranch, StringComparer.Ordinal))
        {
            throw new ArgumentException("Choose an existing local or remote branch to compare.", nameof(baseBranch));
        }
    }

    private static (int Ahead, int Behind) ParseDivergence(string value)
    {
        var fields = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 &&
               int.TryParse(fields[0], out var ahead) &&
               int.TryParse(fields[1], out var behind)
            ? (ahead, behind)
            : (0, 0);
    }

    private static string NormalizeRelativeGitPath(string path) => path.Replace('\\', '/').Trim('"');

    private async Task<ProcessResult> GitAsync(
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("git", arguments, cancellationToken);
        return result.Succeeded ? result : throw new GitCommandException(operation, result);
    }

    private void EnsureWithinManagedRoot(string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(ManagedRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!normalized.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The worktree path is outside the managed worktree root.");
        }
    }

    private static string ValidateRelativePath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("Git file actions require a relative path.", nameof(relativePath));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!full.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The file path escapes the worktree.", nameof(relativePath));
        }

        return Path.GetRelativePath(normalizedRoot, full);
    }

    private static void ValidateBranchName(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (branchName.Any(char.IsWhiteSpace) || branchName.StartsWith('-') || branchName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Git branch name is not valid.", nameof(branchName));
        }
    }

    private static string NormalizeGitPath(string path) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .ToArray();

    private static string DefaultManagedRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "worktrees");
    }
}
