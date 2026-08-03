using CodexDecision.Core.Git;

namespace CodexDecision.Core.Tests;

public sealed class GitCheckpointServiceTests
{
    [Fact]
    public async Task CapturesPreAndPostTreesAndSupportsGuardedUndoRedo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new QueueRunner(
                Ok(root), Ok("stash-before"), Ok("tree-before"),
                Ok("stash-after"), Ok("tree-after"),
                Ok(" 2 files changed"),
                Ok("one.cs\ntwo.cs"), Ok(""), Ok(""),
                Ok("one.cs\ntwo.cs"), Ok(""), Ok(""));
            var service = new GitCheckpointService(runner);

            var before = await service.CaptureBeforeAsync(root);
            var completed = await service.CompleteAsync(before, "turn-1");
            Assert.Contains("2 files", await service.PreviewAsync(completed));

            var undone = await service.UndoAsync(completed);
            Assert.True(undone.IsUndone);
            var redone = await service.RedoAsync(undone);
            Assert.False(redone.IsUndone);
            Assert.Equal(2, runner.Calls.Count(call => call.Contains("restore")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesUndoWhenNewerTrackedChangesExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new QueueRunner(Ok("one.cs"), new ProcessResult(1, "", ""));
            var service = new GitCheckpointService(runner);
            var checkpoint = new CodexDecision.Core.Projects.GitTurnCheckpoint(
                Guid.NewGuid(), root, "tree-before", "tree-after", DateTimeOffset.UtcNow);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UndoAsync(checkpoint));

            Assert.Contains("changed after", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call => call.Contains("restore"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UndoesOnlyTheSelectedTrackedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new QueueRunner(Ok("one.cs\ntwo.cs"), Ok(""), Ok(""));
            var service = new GitCheckpointService(runner);
            var checkpoint = new CodexDecision.Core.Projects.GitTurnCheckpoint(
                Guid.NewGuid(), root, "tree-before", "tree-after", DateTimeOffset.UtcNow);

            var updated = await service.UndoFileAsync(checkpoint, Path.Combine(root, "one.cs"));

            Assert.False(updated.IsUndone);
            Assert.Equal(["one.cs"], updated.IndividuallyUndonePaths);
            var restore = Assert.Single(runner.Calls, call => call.Contains("restore"));
            Assert.Contains("one.cs", restore);
            Assert.DoesNotContain("two.cs", restore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessResult Ok(string output) => new(0, output, "");

    private sealed class QueueRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(results.Dequeue());
        }
    }
}
