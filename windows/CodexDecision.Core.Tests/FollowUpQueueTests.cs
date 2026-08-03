using CodexDecision.Core.Conversations;
using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Tests;

public sealed class FollowUpQueueTests
{
    [Fact]
    public void SupportsMultipleMessagesEditingReorderingAndRemoval()
    {
        var queue = new FollowUpQueue();
        var first = queue.Enqueue("First follow-up");
        var second = queue.Enqueue(
            "Second follow-up",
            new RoutingOverride("gpt-5.6-terra", "medium", PermissionLevel.ReadOnly),
            [SpecialSkillId.PromptMasterCodex],
            AutoRoutingMode.SingleModel);
        var third = queue.Enqueue("Third follow-up");

        Assert.Equal([first.Id, second.Id, third.Id], queue.Snapshot().Select(item => item.Id));
        Assert.True(queue.Edit(second.Id, "Updated second follow-up"));
        Assert.True(queue.MoveUp(third.Id));
        Assert.True(queue.MoveDown(first.Id));

        var reordered = queue.Snapshot();
        Assert.Equal([third.Id, first.Id, second.Id], reordered.Select(item => item.Id));
        Assert.Equal("Updated second follow-up", reordered[2].Prompt);
        Assert.Equal(PermissionLevel.ReadOnly, reordered[2].RoutingOverride?.Permission);
        Assert.Equal([SpecialSkillId.PromptMasterCodex], reordered[2].SpecialSkills);
        Assert.Equal(AutoRoutingMode.SingleModel, reordered[2].AutoRoutingMode);
        Assert.Equal(AutoRoutingMode.Adaptive, reordered[0].AutoRoutingMode);

        Assert.True(queue.Remove(first.Id));
        Assert.Equal([third.Id, second.Id], queue.Snapshot().Select(item => item.Id));
        Assert.Equal(third.Id, queue.Peek()?.Id);
    }

    [Fact]
    public void RejectsBlankPromptsAndLeavesUnknownItemsUntouched()
    {
        var queue = new FollowUpQueue();
        Assert.Throws<ArgumentException>(() => queue.Enqueue("   "));
        Assert.False(queue.Edit(Guid.NewGuid(), "Valid text"));
        Assert.False(queue.MoveUp(Guid.NewGuid()));
        Assert.False(queue.MoveDown(Guid.NewGuid()));
        Assert.False(queue.Remove(Guid.NewGuid()));
        Assert.Empty(queue.Snapshot());
    }
}
