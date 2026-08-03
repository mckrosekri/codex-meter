using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public async Task RoundTripsProjectTaskAndLocksWithoutTranscriptFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-decision-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "workspace.json");
        try
        {
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var state = new WorkspaceState(
                1,
                [
                    new ProjectRecord(
                        projectId,
                        "Sample",
                        [@"C:\code\sample"],
                        true,
                        new RoutingOverride("gpt-5.6-terra", "medium", PermissionLevel.AutoSafe),
                        [
                            new TaskRecord(
                                taskId,
                                "019f-thread",
                                "Implement settings",
                                @"C:\code\sample",
                                false,
                                false,
                                new RoutingOverride(Permission: PermissionLevel.ReadOnly),
                                DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
                                DateTimeOffset.Parse("2026-07-14T12:05:00Z")),
                        ]),
                ],
                FollowUpBehavior.Steer,
                [SpecialSkillId.PromptMasterCodex],
                AutoRoutingMode.SingleModel);

            var store = new WorkspaceStore(path);
            await store.SaveAsync(state);
            var loaded = await store.LoadAsync();

            Assert.Equal(projectId, Assert.Single(loaded.Projects).Id);
            Assert.Equal(taskId, Assert.Single(loaded.Projects[0].Tasks).Id);
            Assert.Equal("019f-thread", loaded.Projects[0].Tasks[0].ThreadId);
            Assert.Equal(PermissionLevel.ReadOnly, loaded.Projects[0].Tasks[0].RoutingLock?.Permission);
            Assert.Equal(FollowUpBehavior.Steer, loaded.FollowUpBehavior);
            Assert.Equal(AutoRoutingMode.SingleModel, loaded.AutoRoutingMode);
            Assert.Equal([SpecialSkillId.PromptMasterCodex], loaded.EnabledSpecialSkills);

            var serialized = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("promptText", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcript", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("messageContent", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("followUpBehavior", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("singleModel", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("promptMasterCodex", serialized, StringComparison.OrdinalIgnoreCase);
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
    public async Task MissingStoreReturnsEmptyVersionedState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "workspace.json");
        var state = await new WorkspaceStore(path).LoadAsync();

        Assert.Equal(2, state.Version);
        Assert.Empty(state.Projects);
        Assert.Equal(AutoRoutingMode.Adaptive, state.AutoRoutingMode);
    }

    [Fact]
    public async Task StoreCreatedBeforeAutoRoutingModeDefaultsToAdaptive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"legacy-workspace-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "workspace.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "version": 1,
                  "projects": [],
                  "followUpBehavior": "steer",
                  "enabledSpecialSkills": []
                }
                """);

            var state = await new WorkspaceStore(path).LoadAsync();

            Assert.Equal(FollowUpBehavior.Steer, state.FollowUpBehavior);
            Assert.Equal(AutoRoutingMode.Adaptive, state.AutoRoutingMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
