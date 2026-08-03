using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Tests;

public sealed class AutomaticDecisionRouterTests
{
    private readonly AutomaticDecisionRouter router = new();

    [Fact]
    public void AutoRoutesNarrowReadTaskToEfficientReadOnlyModel()
    {
        var decision = router.Decide(Request("Summarize this file."));

        Assert.Equal("gpt-5.6-luna", decision.ModelId);
        Assert.Equal("low", decision.Effort);
        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
        Assert.Equal(RoutingSource.Auto, decision.ModelSource);
    }

    [Fact]
    public void ExplicitReadOnlyInstructionOverridesEmbeddedWriteTerms()
    {
        var decision = router.Decide(Request(
            "Summarize the repository in one sentence. Do not edit files."));

        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
        Assert.Equal(RoutingSource.Auto, decision.PermissionSource);
    }

    [Fact]
    public void AutoRoutesImplementationToWorkspaceWrite()
    {
        var decision = router.Decide(Request("Implement the account settings page and its tests."));

        Assert.Equal("gpt-5.6-terra", decision.ModelId);
        Assert.Equal("medium", decision.Effort);
        Assert.Equal(PermissionLevel.WorkspaceWrite, decision.Permission);
        Assert.Equal(RoutingSource.Auto, decision.PermissionSource);
    }

    [Fact]
    public void AutoRoutesComplexWorkToCapabilityModel()
    {
        var decision = router.Decide(Request("Review the production security architecture and threat model."));

        Assert.Equal("gpt-5.6-sol", decision.ModelId);
        Assert.Equal("high", decision.Effort);
        Assert.Equal(TaskComplexity.Complex, decision.Complexity);
        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
    }

    [Fact]
    public void PromptMasterUsesAtLeastPrincipalEngineeringRouteWithoutChangingPermissionIntent()
    {
        var decision = router.Decide(Request("Fix this typo. Do not edit files.") with
        {
            SpecialSkills = [SpecialSkillId.PromptMasterCodex],
        });

        Assert.Equal("gpt-5.6-terra", decision.ModelId);
        Assert.Equal("medium", decision.Effort);
        Assert.Equal(TaskComplexity.Standard, decision.Complexity);
        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
        Assert.Contains("Prompt Master", decision.Reason);
    }

    [Fact]
    public void OneTurnOverrideBeatsTaskAndProjectLocksPerField()
    {
        var request = Request("Implement the change.") with
        {
            ProjectLock = new RoutingOverride("gpt-5.6-sol", "high", PermissionLevel.ReadOnly),
            TaskLock = new RoutingOverride("gpt-5.6-terra", "medium", PermissionLevel.WorkspaceWrite),
            OneTurnOverride = new RoutingOverride("gpt-5.6-luna", "low", PermissionLevel.ReadOnly),
        };

        var decision = router.Decide(request);

        Assert.Equal("gpt-5.6-luna", decision.ModelId);
        Assert.Equal("low", decision.Effort);
        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
        Assert.Equal(RoutingSource.OneTurnOverride, decision.ModelSource);
        Assert.Equal(RoutingSource.OneTurnOverride, decision.PermissionSource);
    }

    [Fact]
    public void TaskLockBeatsProjectLockWhileUnspecifiedPermissionStaysAutomatic()
    {
        var request = Request("Implement the change.") with
        {
            ProjectLock = new RoutingOverride("gpt-5.6-sol", "high", PermissionLevel.ReadOnly),
            TaskLock = new RoutingOverride("gpt-5.6-terra", "medium"),
        };

        var decision = router.Decide(request);

        Assert.Equal("gpt-5.6-terra", decision.ModelId);
        Assert.Equal(RoutingSource.TaskLock, decision.ModelSource);
        Assert.Equal(PermissionLevel.ReadOnly, decision.Permission);
        Assert.Equal(RoutingSource.ProjectLock, decision.PermissionSource);
    }

    [Fact]
    public void LockedUnavailableModelFailsClosed()
    {
        var request = Request("Implement the change.") with
        {
            TaskLock = new RoutingOverride("gpt-missing", "medium"),
        };

        var error = Assert.Throws<LockedModelUnavailableException>(() => router.Decide(request));
        Assert.Equal("gpt-missing", error.ModelId);
    }

    [Fact]
    public void LockedUnsupportedEffortFailsClosed()
    {
        var request = Request("Implement the change.") with
        {
            TaskLock = new RoutingOverride("gpt-5.6-terra", "max"),
        };

        Assert.Throws<LockedEffortUnavailableException>(() => router.Decide(request));
    }

    [Fact]
    public void FullAccessRequiresExplicitConfirmation()
    {
        var unconfirmed = Request("Implement the change.") with
        {
            OneTurnOverride = new RoutingOverride(Permission: PermissionLevel.FullAccess),
        };
        Assert.Throws<FullAccessConfirmationRequiredException>(() => router.Decide(unconfirmed));

        var confirmed = unconfirmed with
        {
            OneTurnOverride = new RoutingOverride(
                Permission: PermissionLevel.FullAccess,
                FullAccessConfirmed: true),
        };
        var decision = router.Decide(confirmed);

        Assert.Equal(PermissionLevel.FullAccess, decision.Permission);
        Assert.Equal(RoutingSource.OneTurnOverride, decision.PermissionSource);
    }

    [Fact]
    public void EcoUsesEfficientRouteAtLowQuotaButNeverOverridesLocks()
    {
        var automatic = router.Decide(Request("Explain the settings implementation.") with
        {
            Policy = RoutingPolicy.Eco,
            RemainingQuotaPercent = 10,
        });
        Assert.Equal("gpt-5.6-luna", automatic.ModelId);

        var locked = router.Decide(Request("Explain the settings implementation.") with
        {
            Policy = RoutingPolicy.Eco,
            RemainingQuotaPercent = 10,
            TaskLock = new RoutingOverride("gpt-5.6-sol", "high"),
        });
        Assert.Equal("gpt-5.6-sol", locked.ModelId);
        Assert.Equal(RoutingSource.TaskLock, locked.ModelSource);
    }

    [Fact]
    public void QualityPromotesNarrowAutomaticTasksToStandardRoute()
    {
        var decision = router.Decide(Request("Summarize this file.") with { Policy = RoutingPolicy.Quality });

        Assert.Equal(TaskComplexity.Standard, decision.Complexity);
        Assert.Equal("gpt-5.6-terra", decision.ModelId);
    }

    private static RoutingRequest Request(string prompt)
    {
        return new RoutingRequest(
            prompt,
            @"C:\code\sample",
            Models());
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
