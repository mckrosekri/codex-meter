using System.Text.RegularExpressions;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Routing;

public sealed partial class AutomaticDecisionRouter
{
    private static readonly string[] WriteTerms =
    [
        "add", "build", "change", "create", "delete", "edit", "fix", "implement", "migrate",
        "modify", "refactor", "remove", "rename", "replace", "update", "write",
    ];

    private static readonly string[] ComplexTerms =
    [
        "architecture", "cross-module", "database migration", "deep", "deployment", "distributed",
        "high risk", "multi-step", "performance", "production", "security", "threat model",
    ];

    private static readonly string[] SimpleTerms =
    [
        "classify", "extract", "format", "rename", "summarize", "typo",
    ];

    private static readonly string[] ExplicitReadOnlyTerms =
    [
        "do not change", "do not edit", "do not modify", "don't change", "don't edit",
        "don't modify", "inspect only", "no changes", "no edits", "read only", "read-only",
        "without changing", "without editing", "without modifying",
    ];

    public RoutingDecision Decide(RoutingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(request.AvailableModels);

        if (request.AvailableModels.Count == 0)
        {
            throw new InvalidOperationException("Codex did not report any available models.");
        }

        var normalizedPrompt = Whitespace().Replace(request.Prompt.Trim().ToLowerInvariant(), " ");
        var complexity = ClassifyComplexity(normalizedPrompt);
        if (request.Policy is RoutingPolicy.Eco &&
            request.RemainingQuotaPercent is < 20 &&
            complexity is TaskComplexity.Standard)
        {
            complexity = TaskComplexity.Simple;
        }
        else if (request.Policy is RoutingPolicy.Quality && complexity is TaskComplexity.Simple)
        {
            complexity = TaskComplexity.Standard;
        }
        var specialSkills = SpecialSkillCatalog.Normalize(request.SpecialSkills);
        if (specialSkills.Contains(SpecialSkillId.PromptMasterCodex) && complexity is TaskComplexity.Simple)
        {
            complexity = TaskComplexity.Standard;
        }
        var autoModel = ChooseAutomaticModel(request.AvailableModels, complexity);
        var autoEffort = ChooseAutomaticEffort(autoModel, complexity);
        var autoPermission = !ContainsAny(normalizedPrompt, ExplicitReadOnlyTerms) &&
                             ContainsAny(normalizedPrompt, WriteTerms)
            ? PermissionLevel.WorkspaceWrite
            : PermissionLevel.ReadOnly;

        var modelChoice = ResolveString(
            autoModel.Id,
            request.ProjectLock?.ModelId,
            request.TaskLock?.ModelId,
            request.OneTurnOverride?.ModelId);

        var model = request.AvailableModels.FirstOrDefault(candidate =>
            candidate.Id.Equals(modelChoice.Value, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            throw new LockedModelUnavailableException(modelChoice.Value);
        }

        var effortChoice = ResolveString(
            model.Id.Equals(autoModel.Id, StringComparison.OrdinalIgnoreCase)
                ? autoEffort
                : NormalizeAutomaticEffort(model, autoEffort),
            request.ProjectLock?.Effort,
            request.TaskLock?.Effort,
            request.OneTurnOverride?.Effort);

        var effort = model.SupportedEfforts.FirstOrDefault(candidate =>
            candidate.Equals(effortChoice.Value, StringComparison.OrdinalIgnoreCase));
        if (effort is null)
        {
            if (effortChoice.Source is not RoutingSource.Auto)
            {
                throw new LockedEffortUnavailableException(model.Id, effortChoice.Value);
            }

            effort = model.DefaultEffort;
        }

        var permissionChoice = ResolvePermission(
            autoPermission,
            request.ProjectLock,
            request.TaskLock,
            request.OneTurnOverride);

        return new RoutingDecision(
            model.Id,
            effort,
            permissionChoice.Value,
            modelChoice.Source,
            effortChoice.Source,
            permissionChoice.Source,
            complexity,
            BuildReason(
                complexity,
                autoPermission,
                modelChoice.Source,
                permissionChoice.Source,
                specialSkills));
    }

    private static TaskComplexity ClassifyComplexity(string prompt)
    {
        if (ContainsAny(prompt, ComplexTerms) || prompt.Length >= 900)
        {
            return TaskComplexity.Complex;
        }

        if (ContainsAny(prompt, SimpleTerms) && prompt.Length < 280)
        {
            return TaskComplexity.Simple;
        }

        return TaskComplexity.Standard;
    }

    private static ModelOption ChooseAutomaticModel(
        IReadOnlyList<ModelOption> models,
        TaskComplexity complexity)
    {
        var familyMarker = complexity switch
        {
            TaskComplexity.Simple => "luna",
            TaskComplexity.Complex => "sol",
            _ => "terra",
        };

        return models.FirstOrDefault(model =>
                   model.Id.Contains(familyMarker, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(model => model.IsDefault)
               ?? models[0];
    }

    private static string ChooseAutomaticEffort(ModelOption model, TaskComplexity complexity)
    {
        var desired = complexity switch
        {
            TaskComplexity.Simple => "low",
            TaskComplexity.Complex => "high",
            _ => "medium",
        };

        return NormalizeAutomaticEffort(model, desired);
    }

    private static string NormalizeAutomaticEffort(ModelOption model, string desired)
    {
        return model.SupportedEfforts.FirstOrDefault(candidate =>
                   candidate.Equals(desired, StringComparison.OrdinalIgnoreCase))
               ?? model.DefaultEffort;
    }

    private static Choice<string> ResolveString(
        string automatic,
        string? project,
        string? task,
        string? oneTurn)
    {
        if (!string.IsNullOrWhiteSpace(oneTurn))
        {
            return new Choice<string>(oneTurn, RoutingSource.OneTurnOverride);
        }

        if (!string.IsNullOrWhiteSpace(task))
        {
            return new Choice<string>(task, RoutingSource.TaskLock);
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            return new Choice<string>(project, RoutingSource.ProjectLock);
        }

        return new Choice<string>(automatic, RoutingSource.Auto);
    }

    private static Choice<PermissionLevel> ResolvePermission(
        PermissionLevel automatic,
        RoutingOverride? project,
        RoutingOverride? task,
        RoutingOverride? oneTurn)
    {
        foreach (var (candidate, source) in new[]
                 {
                     (oneTurn, RoutingSource.OneTurnOverride),
                     (task, RoutingSource.TaskLock),
                     (project, RoutingSource.ProjectLock),
                 })
        {
            if (candidate?.Permission is null or PermissionLevel.AutoSafe)
            {
                continue;
            }

            if (candidate.Permission is PermissionLevel.FullAccess && !candidate.FullAccessConfirmed)
            {
                throw new FullAccessConfirmationRequiredException();
            }

            return new Choice<PermissionLevel>(candidate.Permission.Value, source);
        }

        return new Choice<PermissionLevel>(automatic, RoutingSource.Auto);
    }

    private static bool ContainsAny(string prompt, IEnumerable<string> terms)
    {
        return terms.Any(term => prompt.Contains(term, StringComparison.Ordinal));
    }

    private static string BuildReason(
        TaskComplexity complexity,
        PermissionLevel automaticPermission,
        RoutingSource modelSource,
        RoutingSource permissionSource,
        IReadOnlyList<SpecialSkillId> specialSkills)
    {
        var taskShape = complexity switch
        {
            TaskComplexity.Simple => "a narrow, repeatable task",
            TaskComplexity.Complex => "a complex or high-impact task",
            _ => "an everyday engineering task",
        };
        var permission = automaticPermission is PermissionLevel.WorkspaceWrite
            ? "implementation access"
            : "inspection-only access";

        var skillContext = specialSkills.Contains(SpecialSkillId.PromptMasterCodex)
            ? " Prompt Master will apply principal-engineering interpretation."
            : string.Empty;
        if (modelSource is not RoutingSource.Auto || permissionSource is not RoutingSource.Auto)
        {
            return $"Detected {taskShape}; applied the active user lock over the automatic {permission} route.{skillContext}";
        }

        return $"Detected {taskShape} and selected {permission}.{skillContext}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record Choice<T>(T Value, RoutingSource Source);
}
