using System.Text.Json;
using CodexDecision.Core.AgentBackends;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.AppServer;

/// <summary>
/// Owns replaceable app-server connections. Requests are never replayed after a transport
/// failure because the server may already have accepted them; only subsequent requests use
/// the recovered connection.
/// </summary>
public sealed class CodexConnectionSupervisor : IAgentBackend
{
    private static readonly TimeSpan[] DefaultRecoveryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
    ];

    private readonly Func<CodexAppServerClient> clientFactory;
    private readonly IReadOnlyList<TimeSpan> recoveryDelays;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CodexAppServerClient? client;
    private Task? recoveryTask;
    private bool disposed;

    public CodexConnectionSupervisor(
        Func<CodexAppServerClient>? clientFactory = null,
        IReadOnlyList<TimeSpan>? recoveryDelays = null)
    {
        this.clientFactory = clientFactory ?? (() => new CodexAppServerClient());
        this.recoveryDelays = recoveryDelays ?? DefaultRecoveryDelays;
        if (this.recoveryDelays.Count == 0)
        {
            throw new ArgumentException("At least one recovery delay is required.", nameof(recoveryDelays));
        }
        Snapshot = new AgentBackendSnapshot(
            AgentBackendState.NotStarted,
            0,
            0,
            DateTimeOffset.UtcNow,
            null,
            null);
    }

    public event EventHandler<AppServerEvent>? NotificationReceived;

    public event EventHandler<AppServerRequest>? ApprovalRequested;

    public event EventHandler<Exception>? ProtocolError;

    public event EventHandler<AgentBackendSnapshot>? StateChanged;

    public AgentBackendDescriptor Descriptor { get; } = new(
        "openai-codex",
        "OpenAI Codex",
        "codex-app-server",
        AgentBackendCapabilities.ModelDiscovery |
        AgentBackendCapabilities.PersistentThreads |
        AgentBackendCapabilities.ThreadListing |
        AgentBackendCapabilities.PermissionProfiles |
        AgentBackendCapabilities.ThreadArchiving |
        AgentBackendCapabilities.ThreadRenaming |
        AgentBackendCapabilities.ThreadForking |
        AgentBackendCapabilities.TurnSteering |
        AgentBackendCapabilities.TurnInterruption |
        AgentBackendCapabilities.ContextCompaction |
        AgentBackendCapabilities.Approvals |
        AgentBackendCapabilities.Attachments |
        AgentBackendCapabilities.Skills |
        AgentBackendCapabilities.Plugins |
        AgentBackendCapabilities.McpServers |
        AgentBackendCapabilities.AdaptiveAgents |
        AgentBackendCapabilities.UnattendedTurns);

    public AgentBackendSnapshot Snapshot { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        EnsureConnectedAsync(isRecovery: false, cancellationToken);

    public Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ListModelsAsync(cancellationToken), cancellationToken);

    public Task<PermissionProfileListResponse> ListPermissionProfilesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ListPermissionProfilesAsync(workingDirectory, cancellationToken), cancellationToken);

    public Task<JsonElement> ListSkillsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        bool forceReload = false,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ListSkillsAsync(workingDirectories, forceReload, cancellationToken), cancellationToken);

    public Task<JsonElement> ListPluginsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ListPluginsAsync(workingDirectories, cancellationToken), cancellationToken);

    public Task<JsonElement> SetSkillEnabledAsync(
        string path,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.SetSkillEnabledAsync(path, enabled, cancellationToken), cancellationToken);

    public Task<JsonElement> InstallPluginAsync(
        string pluginName,
        string? marketplacePath = null,
        string? remoteMarketplaceName = null,
        CancellationToken cancellationToken = default) =>
        UseAsync(
            active => active.InstallPluginAsync(
                pluginName,
                marketplacePath,
                remoteMarketplaceName,
                cancellationToken),
            cancellationToken);

    public Task<JsonElement> UninstallPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.UninstallPluginAsync(pluginId, cancellationToken), cancellationToken);

    public Task<JsonElement> ListMcpServersAsync(CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ListMcpServersAsync(cancellationToken), cancellationToken);

    public Task<JsonElement> ReloadMcpServersAsync(CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ReloadMcpServersAsync(cancellationToken), cancellationToken);

    public Task<ThreadListResponse> ListThreadsAsync(
        string? workingDirectory = null,
        bool archived = false,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        UseAsync(
            active => active.ListThreadsAsync(workingDirectory, archived, cursor, limit, cancellationToken),
            cancellationToken);

    public Task<ThreadOperationResponse> StartThreadAsync(
        string workingDirectory,
        RoutingDecision route,
        bool modelIsLocked,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null) =>
        UseAsync(active => active.StartThreadAsync(
            workingDirectory, route, modelIsLocked, cancellationToken, configuration), cancellationToken);

    public Task<ThreadOperationResponse> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null) =>
        UseAsync(active => active.ResumeThreadAsync(
            threadId, workingDirectory, cancellationToken, configuration), cancellationToken);

    public Task<ThreadOperationResponse> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ReadThreadAsync(threadId, cancellationToken), cancellationToken);

    public Task<TurnOperationResponse> StartTurnAsync(
        string threadId,
        string prompt,
        string workingDirectory,
        RoutingDecision route,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null,
        bool unattended = false) =>
        UseAsync(active => active.StartTurnAsync(
            threadId, prompt, workingDirectory, route, cancellationToken, skills, attachments, unattended), cancellationToken);

    public Task<TurnSteerResponse> SteerTurnAsync(
        string threadId,
        string turnId,
        string prompt,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null) =>
        UseAsync(active => active.SteerTurnAsync(
            threadId, turnId, prompt, cancellationToken, skills, attachments), cancellationToken);

    public Task<JsonElement> InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.InterruptTurnAsync(threadId, turnId, cancellationToken), cancellationToken);

    public Task<JsonElement> ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.ArchiveThreadAsync(threadId, cancellationToken), cancellationToken);

    public Task<JsonElement> SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.SetThreadNameAsync(threadId, name, cancellationToken), cancellationToken);

    public Task<ThreadOperationResponse> ForkThreadAsync(
        string threadId,
        string workingDirectory,
        string? lastTurnId = null,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null) =>
        UseAsync(active => active.ForkThreadAsync(
            threadId, workingDirectory, lastTurnId, cancellationToken, configuration), cancellationToken);

    public Task<JsonElement> StartCompactionAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        UseAsync(active => active.StartCompactionAsync(threadId, cancellationToken), cancellationToken);

    public Task RespondToApprovalAsync(
        AppServerRequest request,
        object response,
        CancellationToken cancellationToken = default) =>
        UseAsync(async active =>
        {
            await active.RespondToApprovalAsync(request, response, cancellationToken);
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        CodexAppServerClient? active;
        await gate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetime.Cancel();
            active = client;
            client = null;
            Update(AgentBackendState.Disposed, 0, null, active?.LastDiagnostic);
        }
        finally
        {
            gate.Release();
        }

        if (active is not null)
        {
            Detach(active);
            await active.DisposeAsync();
        }

        if (recoveryTask is not null)
        {
            try
            {
                await recoveryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime.Dispose();
        gate.Dispose();
    }

    private async Task<T> UseAsync<T>(
        Func<CodexAppServerClient, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(isRecovery: false, cancellationToken);
        CodexAppServerClient active;
        await gate.WaitAsync(cancellationToken);
        try
        {
            active = client ?? throw new InvalidOperationException("Codex app-server is not connected.");
        }
        finally
        {
            gate.Release();
        }

        return await operation(active);
    }

    private async Task EnsureConnectedAsync(bool isRecovery, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (client is not null && Snapshot.State is AgentBackendState.Ready)
            {
                return;
            }

            Update(
                isRecovery ? AgentBackendState.Recovering : AgentBackendState.Connecting,
                Snapshot.RecoveryAttempt,
                Snapshot.Error,
                Snapshot.Diagnostic);
            var candidate = clientFactory();
            Attach(candidate);
            try
            {
                await candidate.StartAsync(cancellationToken);
                client = candidate;
                Update(AgentBackendState.Ready, 0, null, candidate.LastDiagnostic, incrementGeneration: true);
            }
            catch
            {
                Detach(candidate);
                await candidate.DisposeAsync();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void Client_ProtocolError(object? sender, Exception error)
    {
        ProtocolError?.Invoke(this, error);
        _ = BeginRecoveryAsync(error, lifetime.Token);
    }

    private async Task BeginRecoveryAsync(Exception cause, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed)
            {
                return;
            }

            if (recoveryTask is { IsCompleted: false })
            {
                return;
            }

            recoveryTask = RecoverLoopAsync(cause, lifetime.Token);
        }
        finally
        {
            gate.Release();
        }

        await recoveryTask;
    }

    private async Task RecoverLoopAsync(Exception cause, CancellationToken cancellationToken)
    {
        CodexAppServerClient? failed;
        await gate.WaitAsync(cancellationToken);
        try
        {
            failed = client;
            client = null;
            Update(AgentBackendState.Recovering, 0, cause.Message, failed?.LastDiagnostic);
        }
        finally
        {
            gate.Release();
        }

        if (failed is not null)
        {
            Detach(failed);
            await failed.DisposeAsync();
        }

        Exception last = cause;
        for (var index = 0; index < recoveryDelays.Count; index++)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                Update(AgentBackendState.Recovering, index + 1, last.Message, Snapshot.Diagnostic);
            }
            finally
            {
                gate.Release();
            }

            await Task.Delay(recoveryDelays[index], cancellationToken);
            try
            {
                await EnsureConnectedAsync(isRecovery: true, cancellationToken);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                last = error;
            }
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            Update(AgentBackendState.Failed, recoveryDelays.Count, last.Message, Snapshot.Diagnostic);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Attach(CodexAppServerClient target)
    {
        target.NotificationReceived += Client_NotificationReceived;
        target.ApprovalRequested += Client_ApprovalRequested;
        target.ProtocolError += Client_ProtocolError;
    }

    private void Detach(CodexAppServerClient target)
    {
        target.NotificationReceived -= Client_NotificationReceived;
        target.ApprovalRequested -= Client_ApprovalRequested;
        target.ProtocolError -= Client_ProtocolError;
    }

    private void Client_NotificationReceived(object? sender, AppServerEvent notification) =>
        NotificationReceived?.Invoke(this, notification);

    private void Client_ApprovalRequested(object? sender, AppServerRequest request) =>
        ApprovalRequested?.Invoke(this, request);

    private void Update(
        AgentBackendState state,
        int recoveryAttempt,
        string? error,
        string? diagnostic,
        bool incrementGeneration = false)
    {
        Snapshot = new AgentBackendSnapshot(
            state,
            Snapshot.Generation + (incrementGeneration ? 1 : 0),
            recoveryAttempt,
            DateTimeOffset.UtcNow,
            error,
            diagnostic);
        StateChanged?.Invoke(this, Snapshot);
    }
}
