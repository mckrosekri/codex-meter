using System.Text.Json;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Conversations;

namespace CodexDecision.Core.Tests;

public sealed class TaskRuntimeCoordinatorTests
{
    [Fact]
    public void RoutesThreadStatusAndTokenUsageToOwningTask()
    {
        var coordinator = new TaskRuntimeCoordinator();
        var session = coordinator.GetOrCreate(Guid.NewGuid(), Guid.NewGuid(), "thread-1");

        coordinator.HandleNotification(Event(
            "thread/status/changed",
            """{"threadId":"thread-1","status":{"type":"active","activeFlags":["waitingOnApproval"]}}"""));
        Assert.Equal(TaskRunState.WaitingForApproval, session.Snapshot.State);

        coordinator.HandleNotification(Event(
            "thread/tokenUsage/updated",
            """{"threadId":"thread-1","turnId":"turn-1","tokenUsage":{"last":{"cachedInputTokens":0,"inputTokens":10,"outputTokens":5,"reasoningOutputTokens":2,"totalTokens":17},"total":{"cachedInputTokens":0,"inputTokens":170,"outputTokens":10,"reasoningOutputTokens":20,"totalTokens":2000},"modelContextWindow":200}}"""));

        Assert.Equal(15, session.Snapshot.Context.TotalTokens);
        Assert.Equal(7.5, session.Snapshot.Context.UsedPercent);
        Assert.Equal(ContextPressure.Normal, session.Snapshot.Context.Pressure);
    }

    [Theory]
    [InlineData(69, ContextPressure.Normal)]
    [InlineData(70, ContextPressure.Advisory)]
    [InlineData(85, ContextPressure.High)]
    [InlineData(95, ContextPressure.Critical)]
    public void AppliesContextPressureThresholds(long tokens, ContextPressure expected)
    {
        var usage = new ThreadTokenUsage(
            new TokenUsageBreakdown(0, tokens, 0, 0, tokens),
            new TokenUsageBreakdown(0, tokens * 10, 0, 0, tokens * 10),
            100);

        Assert.Equal(expected, ContextUsageCalculator.Calculate(usage).Pressure);
    }

    [Fact]
    public void MarksOnlyRunningInactiveTasksQuiet()
    {
        var coordinator = new TaskRuntimeCoordinator();
        var session = coordinator.GetOrCreate(Guid.NewGuid(), Guid.NewGuid(), "thread-1");
        session.SetState(TaskRunState.Running);

        coordinator.UpdateQuietState(DateTimeOffset.UtcNow.AddMinutes(6));

        Assert.True(session.Snapshot.IsQuiet);
    }

    private static AppServerEvent Event(string method, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AppServerEvent(method, document.RootElement.Clone());
    }
}
