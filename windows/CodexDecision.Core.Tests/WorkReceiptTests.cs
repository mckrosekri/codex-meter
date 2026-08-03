using CodexDecision.Core.Conversations;
using CodexDecision.Core.Git;
using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Tests;

public sealed class WorkReceiptTests
{
    [Fact]
    public async Task VerificationGateRunsEveryConfiguredStepAndAggregatesFailure()
    {
        var runner = new RecordingRunner(
            new VerificationCommandResult(0, "tests passed", string.Empty),
            new VerificationCommandResult(1, string.Empty, "lint failed"));
        var gate = new VerificationGateService(runner);
        var policy = new VerificationPolicy(true,
        [
            new VerificationStepDefinition(Guid.NewGuid(), "Tests", "dotnet test", 60),
            new VerificationStepDefinition(Guid.NewGuid(), "Lint", "dotnet format --verify-no-changes", 60),
        ]);

        var result = await gate.RunAsync(policy, ProjectExecutionProfile.Native, Environment.CurrentDirectory);

        Assert.Equal(VerificationGateStatus.Failed, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(VerificationStepStatus.Passed, result.Results[0].Status);
        Assert.Equal(VerificationStepStatus.Failed, result.Results[1].Status);
        Assert.Contains("lint failed", result.Results[1].Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet test", "dotnet format --verify-no-changes"], runner.Commands);
    }

    [Fact]
    public async Task VerificationGateTreatsRunnerErrorsAsEvidenceInsteadOfCrashingTheRun()
    {
        var gate = new VerificationGateService(new ThrowingRunner());
        var policy = new VerificationPolicy(true,
        [
            new VerificationStepDefinition(Guid.NewGuid(), "Unavailable tool", "missing-tool", 30),
        ]);

        var result = await gate.RunAsync(policy, ProjectExecutionProfile.Native, Environment.CurrentDirectory);

        Assert.Equal(VerificationGateStatus.Error, result.Status);
        var step = Assert.Single(result.Results);
        Assert.Equal(VerificationStepStatus.Error, step.Status);
        Assert.Contains("could not start", step.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkReceiptStoreEncryptsOperationalEvidenceAndRoundTripsIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"receipts-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "receipts.json");
        try
        {
            var store = new WorkReceiptStore(new ReverseProtector(), path);
            var receipt = CreateReceipt("private command output");

            await store.SaveStateAsync(new WorkReceiptState([receipt]));

            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("private command output", raw, StringComparison.Ordinal);
            var loaded = Assert.Single((await store.LoadStateAsync()).Receipts);
            Assert.Equal(receipt.Id, loaded.Id);
            Assert.Equal(VerificationGateStatus.Passed, loaded.GateStatus);
            Assert.Equal("private command output", Assert.Single(loaded.Verification).Output);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void EnabledPolicyRequiresCommandsAndBoundsTimeouts()
    {
        Assert.Throws<ArgumentException>(() => new VerificationPolicy(true, []).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VerificationPolicy(true,
            [
                new VerificationStepDefinition(Guid.NewGuid(), "Test", "dotnet test", 2),
            ]).Normalize());
    }

    [Fact]
    public void WorkingTreeFingerprintIsOrderIndependentAndDetectsChangedEvidence()
    {
        var first = new GitReviewFile("src/a.cs", " M", "Modified", false, true, false, 4, 1);
        var second = new GitReviewFile("README.md", "M ", "Modified", true, false, false, 2, 0);
        var left = new GitEnvironmentSnapshot("C:\\repo", "abc", "main", "origin/main", 0, 0, [first, second]);
        var reordered = left with { Files = [second, first] };
        var changed = left with
        {
            Files = [new GitReviewFile("src/a.cs", " M", "Modified", false, true, false, 5, 1), second],
        };

        Assert.Equal(WorkReceiptFingerprint.Create(left), WorkReceiptFingerprint.Create(reordered));
        Assert.NotEqual(WorkReceiptFingerprint.Create(left), WorkReceiptFingerprint.Create(changed));
    }

    private static WorkReceipt CreateReceipt(string output)
    {
        var now = DateTimeOffset.UtcNow;
        var stepId = Guid.NewGuid();
        return new WorkReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "thread-1",
            "turn-1",
            now.AddSeconds(-4),
            now,
            "completed",
            "gpt-test",
            "medium",
            PermissionLevel.WorkspaceWrite,
            1200,
            "C:\\repo",
            "main",
            "abc123",
            "fingerprint",
            2,
            20,
            3,
            VerificationGateStatus.Passed,
            [
                new VerificationStepResult(
                    stepId,
                    "Tests",
                    "dotnet test",
                    VerificationStepStatus.Passed,
                    0,
                    now.AddSeconds(-3),
                    now,
                    output),
            ],
            []);
    }

    private sealed class RecordingRunner(params VerificationCommandResult[] results) : IVerificationCommandRunner
    {
        private readonly Queue<VerificationCommandResult> results = new(results);

        public List<string> Commands { get; } = [];

        public Task<VerificationCommandResult> RunAsync(
            ProjectExecutionProfile profile,
            string workingDirectory,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class ThrowingRunner : IVerificationCommandRunner
    {
        public Task<VerificationCommandResult> RunAsync(
            ProjectExecutionProfile profile,
            string workingDirectory,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The command could not start.");
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Reverse().ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray().Reverse().ToArray();
    }
}
