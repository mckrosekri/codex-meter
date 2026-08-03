using System.Text.Json;
using System.Text.Json.Serialization;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.AppServer;

public sealed record AppServerAgentDefinition(
    string RoleName,
    string Description,
    string ConfigPath,
    IReadOnlyList<string> NicknameCandidates);

public sealed record AppServerThreadConfiguration(
    IReadOnlyList<AppServerAgentDefinition> Agents)
{
    public static AppServerThreadConfiguration Empty { get; } = new([]);
}

public sealed record ReasoningEffortOption(
    [property: JsonPropertyName("reasoningEffort")] string ReasoningEffort,
    [property: JsonPropertyName("description")] string Description);

public sealed record CodexModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("supportedReasoningEfforts")] IReadOnlyList<ReasoningEffortOption> SupportedReasoningEfforts,
    [property: JsonPropertyName("defaultReasoningEffort")] string DefaultReasoningEffort,
    [property: JsonPropertyName("isDefault")] bool IsDefault)
{
    public ModelOption ToRoutingOption()
    {
        return new ModelOption(
            Model,
            DisplayName,
            IsDefault,
            DefaultReasoningEffort,
            SupportedReasoningEfforts.Select(option => option.ReasoningEffort).ToArray());
    }
}

public sealed record ModelListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<CodexModel> Data,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed record PermissionProfileSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("allowed")] bool Allowed);

public sealed record PermissionProfileListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PermissionProfileSummary> Data,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed record CodexThread(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("cwd")] string WorkingDirectory,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("status")] JsonElement Status,
    [property: JsonPropertyName("turns")] IReadOnlyList<CodexTurn> Turns);

public sealed record CodexTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("items")] IReadOnlyList<JsonElement> Items,
    [property: JsonPropertyName("status")] JsonElement Status);

public sealed record ThreadListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<CodexThread> Data,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed record ThreadOperationResponse(
    [property: JsonPropertyName("thread")] CodexThread Thread,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("reasoningEffort")] string? ReasoningEffort = null);

public sealed record TurnOperationResponse(
    [property: JsonPropertyName("turn")] CodexTurn Turn);

public sealed record TurnSteerResponse(
    [property: JsonPropertyName("turnId")] string TurnId);

public sealed class AppServerAttachment
{
    public AppServerAttachment()
    {
    }

    public AppServerAttachment(string path, string? displayName = null, string? detail = null)
    {
        Path = path;
        DisplayName = displayName;
        Detail = detail;
    }

    public string Path { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? Detail { get; set; }

    [JsonIgnore]
    public bool IsImage => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }
        .Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
}

public sealed record TokenUsageBreakdown(
    [property: JsonPropertyName("cachedInputTokens")] long CachedInputTokens,
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens,
    [property: JsonPropertyName("reasoningOutputTokens")] long ReasoningOutputTokens,
    [property: JsonPropertyName("totalTokens")] long TotalTokens);

public sealed record ThreadTokenUsage(
    [property: JsonPropertyName("last")] TokenUsageBreakdown Last,
    [property: JsonPropertyName("total")] TokenUsageBreakdown Total,
    [property: JsonPropertyName("modelContextWindow")] long? ModelContextWindow);

public sealed record ThreadTokenUsageUpdated(
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("tokenUsage")] ThreadTokenUsage TokenUsage);
