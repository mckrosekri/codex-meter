namespace CodexDecision.Core.Routing;

public enum AutoRoutingMode
{
    Adaptive,
    SingleModel,
}

public enum AdaptiveRoutingPhase
{
    Explore,
    Implement,
    Verify,
    Review,
}

public sealed record AdaptiveAgentProfile(
    string RoleName,
    string DisplayName,
    AdaptiveRoutingPhase Phase,
    string ModelId,
    string Effort,
    string Description,
    string DeveloperInstructions,
    IReadOnlyList<string> NicknameCandidates);

public sealed record AdaptiveRoutingPlan(
    AutoRoutingMode Mode,
    bool IsEnabled,
    string CoordinatorModelId,
    string CoordinatorEffort,
    IReadOnlyList<AdaptiveAgentProfile> AgentProfiles,
    string Reason);

public static class AdaptiveAgentCatalog
{
    public static IReadOnlyList<AdaptiveAgentProfile> Create(
        IReadOnlyList<ModelOption> availableModels)
    {
        ArgumentNullException.ThrowIfNull(availableModels);
        if (availableModels.Count == 0)
        {
            throw new InvalidOperationException("Codex did not report any models for adaptive routing.");
        }

        var explorer = ChooseModel(availableModels, "luna", "terra");
        var worker = ChooseModel(availableModels, "terra");
        var verifier = ChooseModel(availableModels, "luna", "terra");
        var reviewer = ChooseModel(availableModels, "sol");

        return
        [
            new AdaptiveAgentProfile(
                "decision_explorer",
                "Explorer",
                AdaptiveRoutingPhase.Explore,
                explorer.Id,
                ChooseEffort(explorer, "low"),
                "Read-heavy repository exploration, file search, log or data gathering, and concise evidence summaries.",
                "Explore only the assigned scope. Prefer efficient searches and targeted reads. Do not edit files. Return a concise evidence-backed summary with file paths, relevant symbols, and unresolved questions.",
                ["Scout", "Beacon", "Sage"]),
            new AdaptiveAgentProfile(
                "decision_worker",
                "Implementation worker",
                AdaptiveRoutingPhase.Implement,
                worker.Id,
                ChooseEffort(worker, "medium"),
                "Well-scoped implementation, mechanical fixes, and focused checks after requirements are clear.",
                "Implement only the bounded assignment. Preserve existing conventions and unrelated user changes. Run focused checks and report changed files, verification results, and any blocker without expanding scope.",
                ["Forge", "Delta", "Mason"]),
            new AdaptiveAgentProfile(
                "decision_verifier",
                "Verifier",
                AdaptiveRoutingPhase.Verify,
                verifier.Id,
                ChooseEffort(verifier, "low"),
                "Deterministic test execution, build verification, result extraction, and failure triage.",
                "Verify the assigned outcome with the narrowest relevant commands. Do not make unrelated edits. Distill failures and logs into actionable evidence for the coordinator.",
                ["Proof", "Gauge", "Check"]),
            new AdaptiveAgentProfile(
                "decision_reviewer",
                "Reviewer",
                AdaptiveRoutingPhase.Review,
                reviewer.Id,
                ChooseEffort(reviewer, "high"),
                "High-judgment review for correctness, architecture, security, edge cases, and completion quality.",
                "Review the completed work like an owner. Prioritize correctness, regressions, security, architectural fit, and missing verification. Return concrete findings first and avoid stylistic churn.",
                ["Atlas", "Aegis", "Oracle"]),
        ];
    }

    private static ModelOption ChooseModel(
        IReadOnlyList<ModelOption> models,
        params string[] familyMarkers)
    {
        foreach (var marker in familyMarkers)
        {
            var match = models.FirstOrDefault(model =>
                model.Id.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return models.FirstOrDefault(model => model.IsDefault) ?? models[0];
    }

    private static string ChooseEffort(ModelOption model, string desired)
    {
        return model.SupportedEfforts.FirstOrDefault(candidate =>
                   candidate.Equals(desired, StringComparison.OrdinalIgnoreCase))
               ?? model.DefaultEffort;
    }
}

public sealed class AdaptiveRoutingPlanner
{
    public AdaptiveRoutingPlan Plan(
        RoutingDecision coordinatorRoute,
        IReadOnlyList<ModelOption> availableModels,
        AutoRoutingMode mode)
    {
        ArgumentNullException.ThrowIfNull(coordinatorRoute);
        var profiles = AdaptiveAgentCatalog.Create(availableModels);

        if (mode is AutoRoutingMode.SingleModel)
        {
            return Disabled(
                coordinatorRoute,
                mode,
                profiles,
                "One-model Full Auto is active, so the coordinator keeps the whole task.");
        }

        if (coordinatorRoute.ModelSource is not RoutingSource.Auto ||
            coordinatorRoute.EffortSource is not RoutingSource.Auto)
        {
            return Disabled(
                coordinatorRoute,
                mode,
                profiles,
                "Adaptive handoffs are suspended because the model or reasoning effort is explicitly selected or locked.");
        }

        if (coordinatorRoute.Complexity is TaskComplexity.Simple)
        {
            return Disabled(
                coordinatorRoute,
                mode,
                profiles,
                "This task is narrow enough that a model handoff would add more overhead than it saves.");
        }

        if (!profiles.Any(profile =>
                !profile.ModelId.Equals(coordinatorRoute.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            return Disabled(
                coordinatorRoute,
                mode,
                profiles,
                "Codex reported no alternate model for a useful adaptive handoff.");
        }

        return new AdaptiveRoutingPlan(
            mode,
            true,
            coordinatorRoute.ModelId,
            coordinatorRoute.Effort,
            profiles,
            "Adaptive phase routing is active; the coordinator may delegate bounded exploration, implementation, verification, and review work to model-specialized agents.");
    }

    private static AdaptiveRoutingPlan Disabled(
        RoutingDecision route,
        AutoRoutingMode mode,
        IReadOnlyList<AdaptiveAgentProfile> profiles,
        string reason)
    {
        return new AdaptiveRoutingPlan(
            mode,
            false,
            route.ModelId,
            route.Effort,
            profiles,
            reason);
    }
}
