using CodexDecision.Core.Git;

namespace CodexDecision.Core.Tests;

public sealed class GitWorktreeServiceTests
{
    [Fact]
    public async Task CreatesDetachedManagedWorktreeFromCleanRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root), Success("abc123"), Success("main"), Success(""),
                Success("Preparing worktree"),
                Success(Path.Combine(root, "managed", "x")), Success("abc123"), Failure(), Success(""));
            var service = new GitWorktreeService(runner, Path.Combine(root, "managed"));

            var metadata = await service.CreateManagedAsync(root, Guid.Empty, "main");

            Assert.Equal("abc123", metadata.BaseCommit);
            Assert.Contains(runner.Calls, call => call.Arguments.Contains("--detach"));
            Assert.DoesNotContain(runner.Calls.SelectMany(call => call.Arguments), argument => argument.Contains('&'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BlocksDirtySourceBeforeCreatingWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(Success(root), Success("abc123"), Success("main"), Success(" M file.cs"));
            var service = new GitWorktreeService(runner, Path.Combine(root, "managed"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateManagedAsync(root, Guid.Empty));

            Assert.Contains("clean", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("worktree"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsFileActionThatEscapesWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-worktree-{Guid.NewGuid():N}");
        var worktree = Path.Combine(root, "managed", "repo", "task");
        Directory.CreateDirectory(worktree);
        try
        {
            var service = new GitWorktreeService(new FakeRunner(), Path.Combine(root, "managed"));
            var metadata = new CodexDecision.Core.Projects.WorktreeMetadata(root, worktree, "HEAD", "abc");

            await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(metadata, "..\\secret.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildsStructuredReviewFromStatusAndNumstat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root),
                Success("abc123"),
                Success("main"),
                Success(" M src/app.cs\nM  README.md\n?? src/new.cs"),
                Success("5\t2\tsrc/app.cs"),
                Success("1\t0\tREADME.md"));
            var service = new GitWorktreeService(runner);

            var review = await service.GetReviewAsync(root);

            Assert.Equal("main", review.BranchName);
            Assert.Equal(3, review.Files.Count);
            Assert.Equal(6, review.TotalAdditions);
            Assert.Equal(2, review.TotalDeletions);
            Assert.Equal("Unstaged", review.Files[0].StageLabel);
            Assert.Equal("Staged", review.Files[1].StageLabel);
            Assert.True(review.Files[2].IsUntracked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadsAnUntrackedFileAsANoIndexDiff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(new ProcessResult(1, "diff --git a/new.cs b/new.cs", ""));
            var service = new GitWorktreeService(runner);

            var diff = await service.GetFileDiffAsync(root, "new.cs", isUntracked: true);

            Assert.Contains("new.cs", diff);
            Assert.Contains(runner.Calls, call => call.Arguments.Contains("--no-index"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildsEnvironmentSnapshotWithUpstreamDivergence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-environment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root),
                Success("abc123"),
                Success("feature/panel"),
                Success(" M src/app.cs"),
                Success("5\t2\tsrc/app.cs"),
                Success(""),
                Success("origin/feature/panel"),
                Success("2\t1"));
            var service = new GitWorktreeService(runner);

            var environment = await service.GetEnvironmentAsync(root);

            Assert.Equal("feature/panel", environment.BranchName);
            Assert.Equal("origin/feature/panel", environment.UpstreamBranch);
            Assert.Equal(2, environment.AheadBy);
            Assert.Equal(1, environment.BehindBy);
            Assert.Equal(5, environment.TotalAdditions);
            Assert.Equal(2, environment.TotalDeletions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildsStructuredComparisonAgainstSelectedBranch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-comparison-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root),
                Success("abc123"),
                Success("feature/panel"),
                Success(""),
                Success("feature/panel\nmain\norigin/main\norigin/HEAD"),
                Success("M\tsrc/app.cs\nA\tREADME.md"),
                Success("3\t1\tsrc/app.cs\n2\t0\tREADME.md"));
            var service = new GitWorktreeService(runner);

            var comparison = await service.GetBranchReviewAsync(root, "main");

            Assert.Equal(2, comparison.Files.Count);
            Assert.Equal(5, comparison.TotalAdditions);
            Assert.Equal(1, comparison.TotalDeletions);
            Assert.All(comparison.Files, file => Assert.Equal("Compared", file.StageLabel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StagesAllFilesBeforeCommittingWhenRequested()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root), Success("abc123"), Success("main"), Success(" M app.cs"),
                Success(""), Success(""));
            var service = new GitWorktreeService(runner);

            await service.CommitAsync(root, "Polish environment panel", stageAll: true);

            Assert.Contains(runner.Calls, call => call.Arguments.Contains("add") && call.Arguments.Contains("-A"));
            Assert.Contains(runner.Calls, call => call.Arguments.Contains("commit") && call.Arguments.Contains("Polish environment panel"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishesBranchWhenNoUpstreamExists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"git-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeRunner(
                Success(root), Success("abc123"), Success("feature/panel"), Success(""),
                Failure(), Success(""));
            var service = new GitWorktreeService(runner);

            await service.PushAsync(root);

            Assert.Contains(runner.Calls, call =>
                call.Arguments.Contains("push") &&
                call.Arguments.Contains("--set-upstream") &&
                call.Arguments.Contains("origin") &&
                call.Arguments.Contains("feature/panel"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessResult Success(string output) => new(0, output, "");

    private static ProcessResult Failure() => new(1, "", "detached");

    private sealed class FakeRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);

        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(results.Count > 0 ? results.Dequeue() : Success(""));
        }
    }
}
