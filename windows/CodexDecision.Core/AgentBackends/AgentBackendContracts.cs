using System.Text.Json;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.AgentBackends;

[Flags]
public enum AgentBackendCapabilities
{
    None = 0,
    ModelDiscovery = 1 << 0,
    PersistentThreads = 1 << 1,
    ThreadListing = 1 << 2,
    PermissionProfiles = 1 << 3,
    ThreadArchiving = 1 << 4,
    ThreadRenaming = 1 << 5,
    ThreadForking = 1 << 6,
    TurnSteering = 1 << 7,
    TurnInterruption = 1 << 8,
    ContextCompaction = 1 << 9,
    Approvals = 1 << 10,
    Attachments = 1 << 11,
    Skills = 1 << 12,
    Plugins = 1 << 13,
    McpServers = 1 << 14,
    AdaptiveAgents = 1 << 15,
    UnattendedTurns = 1 << 16,
}

public sealed record AgentBackendDescriptor(
    string Id,
    string DisplayName,
    string Protocol,
    AgentBackendCapabilities Capabilities)
{
    public bool Supports(AgentBackendCapabilities capability) =>
        (Capabilities & capability) == capability;
}

public enum AgentBackendState
{
    NotStarted,
    Connecting,
    Ready,
    Recovering,
    Failed,
    Disposed,
}

public sealed record AgentBackendSnapshot(
    AgentBackendState State,
    int Generation,
    int RecoveryAttempt,
    DateTimeOffset LastChangedAt,
    string? Error,
    string? Diagnostic);

public sealed class AgentBackendCapabilityException(
    AgentBackendDescriptor backend,
    AgentBackendCapabilities capability)
    : NotSupportedException($"{backend.DisplayName} does not support {capability}.")
{
    public AgentBackendDescriptor Backend { get; } = backend;

    public AgentBackendCapabilities Capability { get; } = capability;
}

/// <summary>
/// Provider-neutral runtime boundary used by the desktop client. Implementations translate
/// their native session, turn, streaming, and approval protocol into the normalized AppServer
/// envelopes consumed by the existing UI.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    AgentBackendDescriptor Descriptor { get; }

    AgentBackendSnapshot Snapshot { get; }

    event EventHandler<AppServerEvent>? NotificationReceived;

    event EventHandler<AppServerRequest>? ApprovalRequested;

    event EventHandler<Exception>? ProtocolError;

    event EventHandler<AgentBackendSnapshot>? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<PermissionProfileListResponse> ListPermissionProfilesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<JsonElement> ListSkillsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        bool forceReload = false,
        CancellationToken cancellationToken = default);

    Task<JsonElement> ListPluginsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SetSkillEnabledAsync(
        string path,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<JsonElement> InstallPluginAsync(
        string pluginName,
        string? marketplacePath = null,
        string? remoteMarketplaceName = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> UninstallPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    Task<JsonElement> ListMcpServersAsync(CancellationToken cancellationToken = default);

    Task<JsonElement> ReloadMcpServersAsync(CancellationToken cancellationToken = default);

    Task<ThreadListResponse> ListThreadsAsync(
        string? workingDirectory = null,
        bool archived = false,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<ThreadOperationResponse> StartThreadAsync(
        string workingDirectory,
        RoutingDecision route,
        bool modelIsLocked,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null);

    Task<ThreadOperationResponse> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null);

    Task<ThreadOperationResponse> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task<TurnOperationResponse> StartTurnAsync(
        string threadId,
        string prompt,
        string workingDirectory,
        RoutingDecision route,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null,
        bool unattended = false);

    Task<TurnSteerResponse> SteerTurnAsync(
        string threadId,
        string turnId,
        string prompt,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null);

    Task<JsonElement> InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<JsonElement> ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default);

    Task<ThreadOperationResponse> ForkThreadAsync(
        string threadId,
        string workingDirectory,
        string? lastTurnId = null,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null);

    Task<JsonElement> StartCompactionAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task RespondToApprovalAsync(
        AppServerRequest request,
        object response,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Convenience base for provider adapters. Only the core session/turn operations are abstract;
/// optional surfaces fail with a capability-specific error until an adapter opts into them.
/// </summary>
public abstract class AgentBackendAdapter : IAgentBackend
{
    private AgentBackendSnapshot snapshot = new(
        AgentBackendState.NotStarted,
        0,
        0,
        DateTimeOffset.UtcNow,
        null,
        null);

    public abstract AgentBackendDescriptor Descriptor { get; }

    public AgentBackendSnapshot Snapshot => snapshot;

    public event EventHandler<AppServerEvent>? NotificationReceived;

    public event EventHandler<AppServerRequest>? ApprovalRequested;

    public event EventHandler<Exception>? ProtocolError;

    public event EventHandler<AgentBackendSnapshot>? StateChanged;

    public abstract Task StartAsync(CancellationToken cancellationToken = default);

    public abstract Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default);

    public virtual Task<PermissionProfileListResponse> ListPermissionProfilesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        Unsupported<PermissionProfileListResponse>(AgentBackendCapabilities.PermissionProfiles);

    public virtual Task<JsonElement> ListSkillsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        bool forceReload = false,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.Skills);

    public virtual Task<JsonElement> ListPluginsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.Plugins);

    public virtual Task<JsonElement> SetSkillEnabledAsync(
        string path,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.Skills);

    public virtual Task<JsonElement> InstallPluginAsync(
        string pluginName,
        string? marketplacePath = null,
        string? remoteMarketplaceName = null,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.Plugins);

    public virtual Task<JsonElement> UninstallPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.Plugins);

    public virtual Task<JsonElement> ListMcpServersAsync(CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.McpServers);

    public virtual Task<JsonElement> ReloadMcpServersAsync(CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.McpServers);

    public virtual Task<ThreadListResponse> ListThreadsAsync(
        string? workingDirectory = null,
        bool archived = false,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        Unsupported<ThreadListResponse>(AgentBackendCapabilities.ThreadListing);

    public abstract Task<ThreadOperationResponse> StartThreadAsync(
        string workingDirectory,
        RoutingDecision route,
        bool modelIsLocked,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null);

    public abstract Task<ThreadOperationResponse> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null);

    public abstract Task<ThreadOperationResponse> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    public abstract Task<TurnOperationResponse> StartTurnAsync(
        string threadId,
        string prompt,
        string workingDirectory,
        RoutingDecision route,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null,
        bool unattended = false);

    public virtual Task<TurnSteerResponse> SteerTurnAsync(
        string threadId,
        string turnId,
        string prompt,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null) =>
        Unsupported<TurnSteerResponse>(AgentBackendCapabilities.TurnSteering);

    public virtual Task<JsonElement> InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.TurnInterruption);

    public virtual Task<JsonElement> ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.ThreadArchiving);

    public virtual Task<JsonElement> SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.ThreadRenaming);

    public virtual Task<ThreadOperationResponse> ForkThreadAsync(
        string threadId,
        string workingDirectory,
        string? lastTurnId = null,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null) =>
        Unsupported<ThreadOperationResponse>(AgentBackendCapabilities.ThreadForking);

    public virtual Task<JsonElement> StartCompactionAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        Unsupported<JsonElement>(AgentBackendCapabilities.ContextCompaction);

    public virtual Task RespondToApprovalAsync(
        AppServerRequest request,
        object response,
        CancellationToken cancellationToken = default) =>
        UnsupportedTask(AgentBackendCapabilities.Approvals);

    public virtual ValueTask DisposeAsync()
    {
        UpdateState(AgentBackendState.Disposed);
        return ValueTask.CompletedTask;
    }

    protected void UpdateState(
        AgentBackendState state,
        string? error = null,
        string? diagnostic = null,
        int recoveryAttempt = 0,
        bool incrementGeneration = false)
    {
        snapshot = new AgentBackendSnapshot(
            state,
            snapshot.Generation + (incrementGeneration ? 1 : 0),
            recoveryAttempt,
            DateTimeOffset.UtcNow,
            error,
            diagnostic);
        StateChanged?.Invoke(this, snapshot);
    }

    protected void PublishNotification(AppServerEvent notification) =>
        NotificationReceived?.Invoke(this, notification);

    protected void RequestApproval(AppServerRequest request) =>
        ApprovalRequested?.Invoke(this, request);

    protected void PublishProtocolError(Exception error) =>
        ProtocolError?.Invoke(this, error);

    protected AgentBackendCapabilityException Unsupported(AgentBackendCapabilities capability) =>
        new(Descriptor, capability);

    private Task<T> Unsupported<T>(AgentBackendCapabilities capability) =>
        Task.FromException<T>(Unsupported(capability));

    private Task UnsupportedTask(AgentBackendCapabilities capability) =>
        Task.FromException(Unsupported(capability));
}
