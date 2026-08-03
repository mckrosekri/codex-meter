using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Tests;

public sealed class AdaptiveRoutingTests
{
    private readonly AdaptiveRoutingPlanner planner = new();

    [Fact]
    public void AutoStandardTaskBuildsModelSpecializedPhasePlan()
    {
        var plan = planner.Plan(
            Route(TaskComplexity.Standard),
            Models(),
            AutoRoutingMode.Adaptive);

        Assert.True(plan.IsEnabled);
        Assert.Equal("gpt-5.6-terra", plan.CoordinatorModelId);
        Assert.Collection(
            plan.AgentProfiles,
            explorer =>
            {
                Assert.Equal("decision_explorer", explorer.RoleName);
                Assert.Equal("gpt-5.6-luna", explorer.ModelId);
                Assert.Equal("low", explorer.Effort);
            },
            worker =>
            {
                Assert.Equal("decision_worker", worker.RoleName);
                Assert.Equal("gpt-5.6-terra", worker.ModelId);
                Assert.Equal("medium", worker.Effort);
            },
            verifier =>
            {
                Assert.Equal("decision_verifier", verifier.RoleName);
                Assert.Equal("gpt-5.6-luna", verifier.ModelId);
                Assert.Equal("low", verifier.Effort);
            },
            reviewer =>
            {
                Assert.Equal("decision_reviewer", reviewer.RoleName);
                Assert.Equal("gpt-5.6-sol", reviewer.ModelId);
                Assert.Equal("high", reviewer.Effort);
            });
    }

    [Fact]
    public void NarrowTaskStaysOnCoordinatorToAvoidHandoffOverhead()
    {
        var plan = planner.Plan(
            Route(TaskComplexity.Simple) with
            {
                ModelId = "gpt-5.6-luna",
                Effort = "low",
            },
            Models(),
            AutoRoutingMode.Adaptive);

        Assert.False(plan.IsEnabled);
        Assert.Contains("overhead", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneModelModeSuppressesAdaptiveHandoffs()
    {
        var plan = planner.Plan(
            Route(TaskComplexity.Complex),
            Models(),
            AutoRoutingMode.SingleModel);

        Assert.False(plan.IsEnabled);
        Assert.Contains("One-model", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RoutingSource.TaskLock, RoutingSource.Auto)]
    [InlineData(RoutingSource.Auto, RoutingSource.ProjectLock)]
    [InlineData(RoutingSource.OneTurnOverride, RoutingSource.OneTurnOverride)]
    public void ExplicitModelOrEffortSelectionSuppressesAdaptiveHandoffs(
        RoutingSource modelSource,
        RoutingSource effortSource)
    {
        var plan = planner.Plan(
            Route(TaskComplexity.Complex) with
            {
                ModelSource = modelSource,
                EffortSource = effortSource,
            },
            Models(),
            AutoRoutingMode.Adaptive);

        Assert.False(plan.IsEnabled);
        Assert.Contains("locked", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleAvailableModelFallsBackWithoutPretendingToHandoff()
    {
        var only = new ModelOption(
            "gpt-only",
            "Only",
            true,
            "medium",
            ["low", "medium", "high"]);
        var plan = planner.Plan(
            Route(TaskComplexity.Complex) with { ModelId = only.Id },
            [only],
            AutoRoutingMode.Adaptive);

        Assert.False(plan.IsEnabled);
        Assert.Contains("no alternate model", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static RoutingDecision Route(TaskComplexity complexity)
    {
        return new RoutingDecision(
            complexity is TaskComplexity.Complex ? "gpt-5.6-sol" : "gpt-5.6-terra",
            complexity is TaskComplexity.Complex ? "high" : "medium",
            PermissionLevel.WorkspaceWrite,
            RoutingSource.Auto,
            RoutingSource.Auto,
            RoutingSource.Auto,
            complexity,
            "Test route");
    }

    private static IReadOnlyList<ModelOption> Models()
    {
        return
        [
            new ModelOption("gpt-5.6-sol", "Sol", true, "medium", ["low", "medium", "high", "max"]),
            new ModelOption("gpt-5.6-terra", "Terra", false, "medium", ["low", "medium", "high"]),
            new ModelOption("gpt-5.6-luna", "Luna", false, "low", ["low", "medium"]),
        ];
    }
}
