using CodexDecision.Core.Conversations;

namespace CodexDecision.Core.Tests;

public sealed class AutomationStoreTests
{
    [Fact]
    public async Task EncryptsScheduledPromptsAndRoundTripsRecurrence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"automation-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "automations.json");
        try
        {
            const string prompt = "private scheduled prompt";
            var store = new AutomationStore(new ReverseProtector(), path);
            var definition = new ScheduledTaskDefinition(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Daily review", prompt,
                ScheduleKind.Daily, 0, new TimeOnly(9, 30), DateTimeOffset.UtcNow.AddDays(1));
            await store.SaveAsync([definition]);

            Assert.DoesNotContain(prompt, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            var loaded = Assert.Single(await store.LoadAsync());
            Assert.Equal(prompt, loaded.Prompt);
            Assert.Equal(new TimeOnly(9, 30), loaded.DailyAt);
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
    public async Task EncryptsMonitorRunHistoryWithTheDefinition()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"automation-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "automations.json");
        try
        {
            var automationId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var store = new AutomationStore(new ReverseProtector(), path);
            var definition = new ScheduledTaskDefinition(
                automationId, projectId, taskId, "PR monitor", "check the pull request",
                ScheduleKind.Interval, 15, null, DateTimeOffset.UtcNow.AddMinutes(15),
                Purpose: ScheduledTaskPurpose.Monitor);
            var run = new AutomationRunRecord(
                Guid.NewGuid(), automationId, projectId, taskId, definition.Name,
                ScheduledTaskPurpose.Monitor, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                AutomationRunStatus.Completed, true, false, "New review feedback found.");

            await store.SaveStateAsync(new AutomationState([definition], [run]));

            var file = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(definition.Prompt, file, StringComparison.Ordinal);
            Assert.DoesNotContain(run.Summary, file, StringComparison.Ordinal);
            var loaded = await store.LoadStateAsync();
            Assert.Equal(ScheduledTaskPurpose.Monitor, Assert.Single(loaded.Definitions).Purpose);
            Assert.False(Assert.Single(loaded.Runs).IsRead);
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
    public void FailedOneTimeAutomationRetriesInsteadOfDisabling()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new ScheduledTaskDefinition(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Once", "prompt",
            ScheduleKind.Once, 0, null, now);

        var advanced = definition.Advance(now, "offline");

        Assert.True(advanced.IsEnabled);
        Assert.Equal(now.AddMinutes(5), advanced.NextRunAt);
        Assert.Equal("offline", advanced.LastError);
    }

    [Theory]
    [InlineData("MONITOR_STATUS: NO_CHANGE\nEverything is healthy.", false, "Everything is healthy.")]
    [InlineData("MONITOR_STATUS: ATTENTION\nThree failures were found.", true, "Three failures were found.")]
    [InlineData("Unstructured monitor output", true, "Unstructured monitor output")]
    public void ClassifiesMonitorAttentionConservatively(string response, bool attention, string summary)
    {
        var result = AutomationResultClassifier.Classify(ScheduledTaskPurpose.Monitor, response);

        Assert.Equal(attention, result.NeedsAttention);
        Assert.Equal(summary, result.Summary);
    }

    [Fact]
    public async Task RejectsUnsupportedStoreVersionsBeforeDecrypting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"automation-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "automations.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{\"version\":99,\"payload\":\"\"}");
            var store = new AutomationStore(new ReverseProtector(), path);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadStateAsync());

            Assert.Contains("version 99", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Reverse().ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray().Reverse().ToArray();
    }
}
