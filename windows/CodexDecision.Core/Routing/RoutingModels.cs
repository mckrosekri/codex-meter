using System.Text.Json.Serialization;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Routing;

public enum PermissionLevel
{
    AutoSafe,
    ReadOnly,
    WorkspaceWrite,
    FullAccess,
}

public enum RoutingSource
{
    Auto,
    ProjectLock,
    TaskLock,
    OneTurnOverride,
}

public enum TaskComplexity
{
    Simple,
    Standard,
    Complex,
}

public enum RoutingPolicy
{
    Eco,
    Balanced,
    Quality,
}

public sealed record ModelOption(
    string Id,
    string DisplayName,
    bool IsDefault,
    string DefaultEffort,
    IReadOnlyList<string> SupportedEfforts);

public sealed record RoutingOverride(
    string? ModelId = null,
    string? Effort = null,
    PermissionLevel? Permission = null,
    bool FullAccessConfirmed = false)
{
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ModelId) &&
        string.IsNullOrWhiteSpace(Effort) &&
        Permission is null or PermissionLevel.AutoSafe;
}

public sealed record RoutingRequest(
    string Prompt,
    string WorkingDirectory,
    IReadOnlyList<ModelOption> AvailableModels,
    RoutingOverride? ProjectLock = null,
    RoutingOverride? TaskLock = null,
    RoutingOverride? OneTurnOverride = null,
    IReadOnlyList<SpecialSkillId>? SpecialSkills = null,
    RoutingPolicy Policy = RoutingPolicy.Balanced,
    double? RemainingQuotaPercent = null);

public sealed record RoutingDecision(
    string ModelId,
    string Effort,
    PermissionLevel Permission,
    RoutingSource ModelSource,
    RoutingSource EffortSource,
    RoutingSource PermissionSource,
    TaskComplexity Complexity,
    string Reason);

public sealed class LockedModelUnavailableException(string modelId)
    : InvalidOperationException($"The locked model '{modelId}' is not available.")
{
    public string ModelId { get; } = modelId;
}

public sealed class LockedEffortUnavailableException(string modelId, string effort)
    : InvalidOperationException($"The locked reasoning effort '{effort}' is not available for '{modelId}'.")
{
    public string ModelId { get; } = modelId;

    public string Effort { get; } = effort;
}

public sealed class FullAccessConfirmationRequiredException()
    : InvalidOperationException("Full access requires an explicit user confirmation.");
