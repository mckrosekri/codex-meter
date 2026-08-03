using System.Collections.Concurrent;
using System.Text.Json;
using CodexDecision.Core.AgentBackends;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.Git;
using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;

namespace CodexMeterTray.Services;

public enum CodexConnectionState
{
    NotStarted,
    Connecting,
    Ready,
    Recovering,
    Failed,
}

public sealed record CodexThreadIndexEntry(
    string ThreadId,
    string Title,
    string Preview,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived,
    Guid? ProjectId,
    Guid? TaskId,
    string? ProjectName);

public sealed class AppServices : IAsyncDisposable
{
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly SemaphoreSlim threadIndexGate = new(1, 1);
    private readonly SemaphoreSlim automationGate = new(1, 1);
    private readonly SemaphoreSlim receiptGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, GoalDefinition?> goals = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> continuityGates = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lastHealthProbe = new();
    private readonly ConcurrentDictionary<Guid, string> draftPrompts = new();
    private readonly ConcurrentDictionary<string, Guid> activeAutomationRuns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> turnStartedAt =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> receiptRunGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AppServerRequest>> pendingApprovals =
        new(StringComparer.Ordinal);
    private readonly AdaptiveAgentConfigurationStore adaptiveAgentStore = new();
    private readonly NativeCodexWorkspaceCatalog nativeWorkspaceCatalog = new();
    private readonly NativeCodexSessionIndexCatalog nativeSessionIndexCatalog = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SecureContinuityStore continuityStore;
    private readonly AutomationStore automationStore;
    private readonly WorkReceiptStore workReceiptStore;
    private Task? healthMonitor;
    private bool hydratingContinuity;
    private bool initialized;

    public AppServices(IAgentBackend? backend = null)
    {
        Workspace = new WorkspaceCoordinator(new WorkspaceStore());
        Router = new AutomaticDecisionRouter();
        AdaptiveRouter = new AdaptiveRoutingPlanner();
        Backend = backend ?? new CodexConnectionSupervisor();
        TaskRuntimes = new TaskRuntimeCoordinator();
        TaskAttention = new TaskAttentionTracker();
        Worktrees = new GitWorktreeService();
        Checkpoints = new GitCheckpointService();
        ExecutionProfiles = new ExecutionProfileService();
        continuityStore = new SecureContinuityStore(new DpapiSecretProtector());
        automationStore = new AutomationStore(new DpapiSecretProtector());
        workReceiptStore = new WorkReceiptStore(new DpapiSecretProtector());
        VerificationGate = new VerificationGateService();

        Backend.ProtocolError += (_, error) =>
        {
            LastError = error.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Backend.NotificationReceived += Backend_NotificationReceived;
        Backend.ApprovalRequested += (_, request) =>
        {
            var threadId = ReadString(request.Parameters, "threadId")
                           ?? ReadString(request.Parameters, "conversationId");
            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            var key = request.Id.ToString();
            pendingApprovals.GetOrAdd(
                threadId,
                _ => new ConcurrentDictionary<string, AppServerRequest>(StringComparer.Ordinal))[key] = request;
            TaskRuntimes.FindByThread(threadId)?.SetState(TaskRunState.WaitingForApproval);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Backend.StateChanged += Backend_StateChanged;
        TaskRuntimes.SnapshotChanged += (_, snapshot) =>
        {
            TaskAttention.Observe(snapshot);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public WorkspaceCoordinator Workspace { get; }

    public AutomaticDecisionRouter Router { get; }

    public AdaptiveRoutingPlanner AdaptiveRouter { get; }

    public IAgentBackend Backend { get; }

    public TaskRuntimeCoordinator TaskRuntimes { get; }

    public TaskAttentionTracker TaskAttention { get; }

    public GitWorktreeService Worktrees { get; }

    public GitCheckpointService Checkpoints { get; }

    public ExecutionProfileService ExecutionProfiles { get; }

    public VerificationGateService VerificationGate { get; }

    public AppServerThreadConfiguration AdaptiveThreadConfiguration { get; private set; } =
        AppServerThreadConfiguration.Empty;

    public string? AdaptiveRoutingError { get; private set; }

    public IReadOnlyList<CodexModel> Models { get; private set; } = [];

    public IReadOnlyList<PermissionProfileSummary> PermissionProfiles { get; private set; } = [];

    public IReadOnlyList<CodexThreadIndexEntry> ThreadIndex { get; private set; } = [];

    public CodexConnectionState ConnectionState { get; private set; } = CodexConnectionState.NotStarted;

    public string? LastError { get; private set; }

    public IReadOnlyList<ContinuityWarning> ContinuityWarnings => continuityStore.Warnings;

    public IReadOnlyList<ScheduledTaskDefinition> Automations { get; private set; } = [];

    public IReadOnlyList<AutomationRunRecord> AutomationRuns { get; private set; } = [];

    public IReadOnlyList<WorkReceipt> WorkReceipts { get; private set; } = [];

    public WorkReceipt? GetLatestWorkReceipt(Guid taskId) =>
        WorkReceipts.Where(receipt => receipt.TaskId == taskId)
            .OrderByDescending(receipt => receipt.CompletedAt)
            .FirstOrDefault();

    public async Task SetVerificationPolicyAsync(
        Guid projectId,
        VerificationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await Workspace.SetProjectVerificationPolicyAsync(projectId, policy, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<WorkReceipt> FinalizeWorkReceiptAsync(
        Guid projectId,
        Guid taskId,
        string? turnId,
        string turnStatus,
        bool isAutomated = false,
        DateTimeOffset? startedAt = null,
        CancellationToken cancellationToken = default)
    {
        var key = $"{taskId:N}:{turnId ?? "manual"}";
        var runGate = receiptRunGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await runGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(turnId) &&
                WorkReceipts.FirstOrDefault(receipt =>
                    receipt.TaskId == taskId &&
                    string.Equals(receipt.TurnId, turnId, StringComparison.Ordinal)) is { } existing)
            {
                if (isAutomated && !existing.IsAutomated)
                {
                    existing = existing with { IsAutomated = true };
                    await SaveWorkReceiptAsync(existing, cancellationToken);
                }
                return existing;
            }

            var project = Workspace.GetProject(projectId);
            var task = Workspace.GetTask(projectId, taskId);
            var snapshot = TaskRuntimes.Find(taskId)?.Snapshot;
            var policy = project.VerificationPolicy ?? VerificationPolicy.Disabled;
            var completed = string.Equals(turnStatus, "completed", StringComparison.OrdinalIgnoreCase);
            var gate = completed
                ? await VerificationGate.RunAsync(
                    policy,
                    project.ExecutionProfile ?? ProjectExecutionProfile.Native,
                    task.WorkingDirectory,
                    cancellationToken)
                : (policy.IsEnabled ? VerificationGateStatus.Skipped : VerificationGateStatus.NotConfigured,
                    (IReadOnlyList<VerificationStepResult>)[]);
            GitEnvironmentSnapshot? environment = null;
            try
            {
                // Verification tools can format or generate files, so capture Git
                // evidence only after the final configured command exits.
                environment = await Worktrees.GetEnvironmentAsync(task.WorkingDirectory, cancellationToken);
            }
            catch (Exception error) when (error is GitCommandException or DirectoryNotFoundException)
            {
                // Receipts are still useful for non-Git projects.
            }
            var completedAt = DateTimeOffset.UtcNow;
            DateTimeOffset? recordedStartedAt = null;
            if (!string.IsNullOrWhiteSpace(turnId) && turnStartedAt.TryRemove(turnId, out var recorded))
            {
                recordedStartedAt = recorded;
            }
            var resolvedStartedAt = startedAt
                                    ?? recordedStartedAt
                                    ?? snapshot?.LastActivityAt
                                    ?? completedAt;
            var receipt = new WorkReceipt(
                Guid.NewGuid(),
                projectId,
                taskId,
                task.ThreadId,
                turnId,
                resolvedStartedAt,
                completedAt,
                turnStatus,
                snapshot?.Route?.ModelId,
                snapshot?.Route?.Effort,
                snapshot?.Route?.Permission,
                snapshot?.Context.TotalTokens ?? 0,
                environment?.RepositoryRoot,
                environment?.BranchName,
                environment?.HeadCommit,
                environment is null ? null : WorkReceiptFingerprint.Create(environment),
                environment?.Files.Count ?? 0,
                environment?.TotalAdditions ?? 0,
                environment?.TotalDeletions ?? 0,
                gate.Item1,
                gate.Item2,
                BuildRiskFlags(turnStatus, snapshot?.Route, environment, gate.Item1),
                isAutomated);
            await SaveWorkReceiptAsync(receipt, cancellationToken);
            return receipt;
        }
        finally
        {
            runGate.Release();
            receiptRunGates.TryRemove(key, out _);
        }
    }

    public async Task<WorkReceipt> RerunVerificationAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var project = Workspace.GetProject(projectId);
        var task = Workspace.GetTask(projectId, taskId);
        var policy = project.VerificationPolicy ?? VerificationPolicy.Disabled;
        if (!policy.IsEnabled)
        {
            throw new InvalidOperationException("Enable the verification gate and add at least one command first.");
        }

        var latest = GetLatestWorkReceipt(taskId)
                     ?? throw new InvalidOperationException("A work receipt will be available after the first completed task turn.");
        var gate = await VerificationGate.RunAsync(
            policy,
            project.ExecutionProfile ?? ProjectExecutionProfile.Native,
            task.WorkingDirectory,
            cancellationToken);
        // Capture the tree produced by the checks, not the tree that preceded them.
        var environment = await Worktrees.GetEnvironmentAsync(task.WorkingDirectory, cancellationToken);
        var updated = latest with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            RepositoryRoot = environment.RepositoryRoot,
            BranchName = environment.BranchName,
            HeadCommit = environment.HeadCommit,
            WorkingTreeFingerprint = WorkReceiptFingerprint.Create(environment),
            ChangedFileCount = environment.Files.Count,
            Additions = environment.TotalAdditions,
            Deletions = environment.TotalDeletions,
            GateStatus = gate.Status,
            Verification = gate.Results,
            RiskFlags = BuildRiskFlags(latest.TurnStatus, TaskRuntimes.Find(taskId)?.Snapshot.Route, environment, gate.Status),
        };
        await SaveWorkReceiptAsync(updated, cancellationToken);
        return updated;
    }

    private async Task SaveWorkReceiptAsync(WorkReceipt receipt, CancellationToken cancellationToken)
    {
        await receiptGate.WaitAsync(cancellationToken);
        try
        {
            WorkReceipts = new[] { receipt }
                .Concat(WorkReceipts.Where(candidate => candidate.Id != receipt.Id))
                .OrderByDescending(candidate => candidate.CompletedAt)
                .Take(500)
                .ToArray();
            await workReceiptStore.SaveStateAsync(new WorkReceiptState(WorkReceipts), cancellationToken);
        }
        finally
        {
            receiptGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<string> BuildRiskFlags(
        string turnStatus,
        RoutingDecision? route,
        GitEnvironmentSnapshot? environment,
        VerificationGateStatus gateStatus)
    {
        var risks = new List<string>();
        if (!string.Equals(turnStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            risks.Add($"Codex turn ended with status '{turnStatus}'.");
        }
        if (route?.Permission is PermissionLevel.FullAccess)
        {
            risks.Add("This turn used full-access permission.");
        }
        if ((environment?.TotalDeletions ?? 0) >= 250)
        {
            risks.Add($"Large deletion: {environment!.TotalDeletions:N0} lines removed.");
        }
        if (gateStatus is VerificationGateStatus.Failed or VerificationGateStatus.Error)
        {
            risks.Add("One or more verification checks did not pass.");
        }
        return risks;
    }

    public TaskRuntimeSession GetTaskRuntime(Guid projectId, Guid taskId)
    {
        var task = Workspace.GetTask(projectId, taskId);
        return RegisterRuntime(projectId, task);
    }

    public GoalDefinition? GetGoal(Guid taskId) => goals.GetValueOrDefault(taskId);

    public void SetDraftPrompt(Guid taskId, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        draftPrompts[taskId] = prompt.Trim();
    }

    public string? TakeDraftPrompt(Guid taskId) =>
        draftPrompts.TryRemove(taskId, out var prompt) ? prompt : null;

    public IReadOnlyList<AppServerRequest> GetPendingApprovals(string threadId) =>
        pendingApprovals.TryGetValue(threadId, out var requests)
            ? requests.Values.ToArray()
            : [];

    public void MarkApprovalHandled(string threadId, AppServerRequest request)
    {
        if (!pendingApprovals.TryGetValue(threadId, out var requests))
        {
            return;
        }

        requests.TryRemove(request.Id.ToString(), out _);
        if (requests.IsEmpty)
        {
            pendingApprovals.TryRemove(threadId, out _);
        }
    }

    public async Task SaveGoalAsync(
        Guid projectId,
        Guid taskId,
        GoalDefinition? goal,
        CancellationToken cancellationToken = default)
    {
        _ = Workspace.GetTask(projectId, taskId);
        goals[taskId] = goal?.Normalize();
        await SaveContinuityAsync(taskId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSecureContinuityAsync(
        SecureContinuityMode mode,
        CancellationToken cancellationToken = default)
    {
        await Workspace.SetSecureContinuityModeAsync(mode, cancellationToken);
        if (mode is SecureContinuityMode.Disabled)
        {
            foreach (var task in Workspace.State.Projects.SelectMany(project => project.Tasks))
            {
                await continuityStore.DeleteTaskAsync(task.Id, cancellationToken);
            }
        }
        else
        {
            foreach (var task in Workspace.State.Projects.SelectMany(project => project.Tasks))
            {
                await SaveContinuityAsync(task.Id, cancellationToken);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ScheduledTaskDefinition> AddAutomationAsync(
        Guid projectId,
        Guid taskId,
        string name,
        string prompt,
        ScheduleKind kind,
        int intervalMinutes,
        TimeOnly? dailyAt,
        DateTimeOffset firstRunAt,
        ScheduledTaskPurpose purpose = ScheduledTaskPurpose.FollowUp,
        CancellationToken cancellationToken = default)
    {
        _ = Workspace.GetTask(projectId, taskId);
        var definition = new ScheduledTaskDefinition(
            Guid.NewGuid(), projectId, taskId, name, prompt, kind,
            intervalMinutes, dailyAt, firstRunAt,
            Purpose: purpose).Normalize();
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            Automations = [.. Automations, definition];
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return definition;
    }

    public async Task<ScheduledTaskDefinition> UpdateAutomationAsync(
        Guid id,
        Guid projectId,
        Guid taskId,
        string name,
        string prompt,
        ScheduleKind kind,
        int intervalMinutes,
        TimeOnly? dailyAt,
        DateTimeOffset nextRunAt,
        ScheduledTaskPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        _ = Workspace.GetTask(projectId, taskId);
        await automationGate.WaitAsync(cancellationToken);
        ScheduledTaskDefinition updated;
        try
        {
            var current = Automations.FirstOrDefault(candidate => candidate.Id == id)
                          ?? throw new KeyNotFoundException($"Scheduled task '{id}' was not found.");
            updated = current with
            {
                ProjectId = projectId,
                TaskId = taskId,
                Name = name,
                Prompt = prompt,
                Kind = kind,
                IntervalMinutes = intervalMinutes,
                DailyAt = dailyAt,
                NextRunAt = nextRunAt,
                Purpose = purpose,
                LastError = null,
            };
            updated = updated.Normalize();
            Automations = Automations.Select(candidate => candidate.Id == id ? updated : candidate).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public async Task SetAutomationEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            Automations = Automations.Select(definition => definition.Id == id
                ? definition with
                {
                    IsEnabled = enabled,
                    LastError = null,
                    NextRunAt = enabled && definition.NextRunAt <= DateTimeOffset.UtcNow
                        ? DateTimeOffset.UtcNow.AddMinutes(1)
                        : definition.NextRunAt,
                }
                : definition).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAutomationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            Automations = Automations.Where(definition => definition.Id != id).ToArray();
            AutomationRuns = AutomationRuns.Where(run => run.AutomationId != id).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunAutomationNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = Automations.FirstOrDefault(candidate => candidate.Id == id)
                         ?? throw new KeyNotFoundException($"Scheduled task '{id}' was not found.");
        await StartAutomationRunAsync(definition, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkAutomationRunReadAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            AutomationRuns = AutomationRuns.Select(run => run.Id == runId
                ? run with { IsRead = true }
                : run).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkAllAutomationRunsReadAsync(CancellationToken cancellationToken = default)
    {
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            AutomationRuns = AutomationRuns.Select(run => run.NeedsAttention
                ? run with { IsRead = true }
                : run).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static IReadOnlyList<AppServerSkillReference> ResolveSpecialSkills(
        IEnumerable<SpecialSkillId>? skillIds)
    {
        return SpecialSkillCatalog.Normalize(skillIds).Select(skillId =>
        {
            var definition = SpecialSkillCatalog.Get(skillId);
            return ResolveSkillReference(
                definition.Slug,
                definition.RelativeSkillPath,
                definition.DisplayName);
        }).ToArray();
    }

    public AdaptiveRoutingPlan CreateAdaptiveRoutingPlan(
        RoutingDecision coordinatorRoute,
        AutoRoutingMode mode)
    {
        if (!Backend.Descriptor.Supports(AgentBackendCapabilities.AdaptiveAgents))
        {
            return new AdaptiveRoutingPlan(
                mode,
                false,
                coordinatorRoute.ModelId,
                coordinatorRoute.Effort,
                [],
                $"{Backend.Descriptor.DisplayName} does not expose adaptive worker agents, so this turn will stay on one model.");
        }

        var plan = AdaptiveRouter.Plan(
            coordinatorRoute,
            Models.Select(model => model.ToRoutingOption()).ToArray(),
            mode);
        if (!plan.IsEnabled || AdaptiveThreadConfiguration.Agents.Count > 0)
        {
            return plan;
        }

        return plan with
        {
            IsEnabled = false,
            Reason = AdaptiveRoutingError is null
                ? "Adaptive agent configuration is unavailable, so this turn will stay on one model."
                : $"Adaptive agent configuration is unavailable, so this turn will stay on one model. {AdaptiveRoutingError}",
        };
    }

    public IReadOnlyList<AppServerSkillReference> ResolveTurnSkills(
        IEnumerable<SpecialSkillId>? specialSkillIds,
        AdaptiveRoutingPlan adaptivePlan)
    {
        if (!Backend.Descriptor.Supports(AgentBackendCapabilities.Skills))
        {
            return [];
        }

        var skills = ResolveSpecialSkills(specialSkillIds).ToList();
        if (adaptivePlan.IsEnabled)
        {
            skills.Add(ResolveSkillReference(
                "adaptive-routing-codex",
                Path.Combine("SpecialSkills", "AdaptiveRoutingCodex", "SKILL.md"),
                "adaptive routing"));
        }

        return skills;
    }

    public void NotifyWorkspaceChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task InitializeAsync(
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (!initialized)
            {
                string? startupWarning = null;
                ConnectionState = CodexConnectionState.Connecting;
                Changed?.Invoke(this, EventArgs.Empty);
                await Workspace.InitializeAsync(cancellationToken);
                await InitializeRuntimeStateAsync(cancellationToken);
                try
                {
                    var automationState = await automationStore.LoadStateAsync(cancellationToken);
                    Automations = automationState.Definitions;
                    AutomationRuns = automationState.Runs.Select(run =>
                            run.Status is AutomationRunStatus.Running
                                ? run with
                                {
                                    Status = AutomationRunStatus.Failed,
                                    CompletedAt = DateTimeOffset.UtcNow,
                                    NeedsAttention = true,
                                    IsRead = false,
                                    Summary = "The app closed before this scheduled run completed.",
                                    Error = "Interrupted before completion.",
                                }
                                : run)
                        .ToArray();
                    if (AutomationRuns.Any(run => run.Error == "Interrupted before completion."))
                    {
                        await automationStore.SaveStateAsync(
                            new AutomationState(Automations, AutomationRuns),
                            cancellationToken);
                    }

                }
                catch (Exception error)
                {
                    // A damaged optional schedule must not prevent the user from
                    // opening the app and recovering their normal conversations.
                    Automations = [];
                    startupWarning = $"Scheduled tasks could not be loaded: {error.Message}";
                }

                try
                {
                    WorkReceipts = (await workReceiptStore.LoadStateAsync(cancellationToken)).Receipts
                        .OrderByDescending(receipt => receipt.CompletedAt)
                        .Take(500)
                        .ToArray();
                }
                catch (Exception error)
                {
                    WorkReceipts = [];
                    startupWarning = string.Join(
                        " ",
                        new[] { startupWarning, $"Work receipts could not be loaded: {error.Message}" }
                            .Where(message => !string.IsNullOrWhiteSpace(message)));
                }

                await Backend.StartAsync(cancellationToken);
                await RefreshBackendMetadataAsync(workingDirectory, cancellationToken);
                await RefreshThreadIndexAsync(cancellationToken);
                initialized = true;
                LastError = startupWarning;
                ConnectionState = CodexConnectionState.Ready;
                healthMonitor = MonitorTaskHealthAsync(lifetime.Token);
            }
            else if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                PermissionProfiles = Backend.Descriptor.Supports(AgentBackendCapabilities.PermissionProfiles)
                    ? (await Backend.ListPermissionProfilesAsync(workingDirectory, cancellationToken)).Data
                    : [];
            }
        }
        catch (Exception error)
        {
            LastError = error.Message;
            ConnectionState = CodexConnectionState.Failed;
            throw;
        }
        finally
        {
            initializeGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SyncProjectThreadsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = Workspace.GetProject(projectId);
        await RefreshThreadIndexAsync(cancellationToken);
    }

    public async Task RefreshThreadIndexAsync(CancellationToken cancellationToken = default)
    {
        await threadIndexGate.WaitAsync(cancellationToken);
        try
        {
            var nativeWorkspace = await nativeWorkspaceCatalog.LoadAsync(cancellationToken);
            var nativeSessions = await nativeSessionIndexCatalog.LoadAsync(cancellationToken);
            var existingTasks = Workspace.State.Projects
                .SelectMany(project => project.Tasks
                    .Where(task => !string.IsNullOrWhiteSpace(task.ThreadId))
                    .Select(task => (project, task)))
                .GroupBy(pair => pair.task.ThreadId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var nativeThreads = new List<NativeThreadRecord>();
            foreach (var session in nativeSessions)
            {
                if (existingTasks.TryGetValue(session.ThreadId, out var existing))
                {
                    nativeThreads.Add(new NativeThreadRecord(
                        session.ThreadId,
                        session.Title,
                        existing.task.WorkingDirectory,
                        existing.task.CreatedAt,
                        session.UpdatedAt > existing.task.UpdatedAt ? session.UpdatedAt : existing.task.UpdatedAt,
                        existing.task.IsArchived,
                        nativeWorkspace.ResolveProjectDirectory(session.ThreadId, existing.task.WorkingDirectory)));
                    continue;
                }

                var projectDirectory = nativeWorkspace.ThreadProjectDirectories.GetValueOrDefault(session.ThreadId)
                                       ?? nativeWorkspace.ThreadWorkspaceRootHints.GetValueOrDefault(session.ThreadId);
                if (string.IsNullOrWhiteSpace(projectDirectory))
                {
                    continue;
                }

                nativeThreads.Add(new NativeThreadRecord(
                    session.ThreadId,
                    session.Title,
                    projectDirectory,
                    session.UpdatedAt,
                    session.UpdatedAt,
                    IsArchived: false,
                    projectDirectory));
            }

            foreach (var existing in existingTasks.Values.Where(existing =>
                         nativeSessions.All(session => !string.Equals(
                             session.ThreadId,
                             existing.task.ThreadId,
                             StringComparison.Ordinal))))
            {
                nativeThreads.Add(new NativeThreadRecord(
                    existing.task.ThreadId!,
                    existing.task.Title,
                    existing.task.WorkingDirectory,
                    existing.task.CreatedAt,
                    existing.task.UpdatedAt,
                    existing.task.IsArchived,
                    nativeWorkspace.ResolveProjectDirectory(existing.task.ThreadId!, existing.task.WorkingDirectory)));
            }

            await Workspace.SynchronizeNativeThreadsAsync(
                nativeThreads,
                nativeWorkspace.Projects,
                cancellationToken);
            ThreadIndex = Workspace.State.Projects
                .SelectMany(project => project.Tasks
                    .Where(task => !string.IsNullOrWhiteSpace(task.ThreadId))
                    .Select(task => new CodexThreadIndexEntry(
                        task.ThreadId!,
                        task.Title,
                        string.Empty,
                        task.WorkingDirectory,
                        task.CreatedAt,
                        task.UpdatedAt,
                        task.IsArchived,
                        project.Id,
                        task.Id,
                        project.Name)))
                .GroupBy(entry => entry.ThreadId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(entry => entry.IsArchived).First())
                .OrderByDescending(entry => entry.UpdatedAt)
                .ToArray();
            foreach (var entry in ThreadIndex.Where(entry =>
                         !entry.IsArchived && entry.ProjectId is not null && entry.TaskId is not null))
            {
                RegisterRuntime(entry.ProjectId!.Value, Workspace.GetTask(entry.ProjectId.Value, entry.TaskId!.Value));
            }
        }
        finally
        {
            threadIndexGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CodexThreadIndexEntry> EnsureIndexedThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var entry = ThreadIndex.FirstOrDefault(candidate =>
            string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal));
        if (entry?.ProjectId is null || entry.TaskId is null)
        {
            await RefreshThreadIndexAsync(cancellationToken);
            entry = ThreadIndex.FirstOrDefault(candidate =>
                string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal));
        }

        if (entry is null)
        {
            throw new KeyNotFoundException($"Codex chat '{threadId}' is not in the current native index.");
        }

        if (entry.ProjectId is null || entry.TaskId is null)
        {
            throw new InvalidOperationException(
                "This native Codex chat could not be mapped to its recorded working directory.");
        }

        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        if (Workspace.State.SecureContinuityMode is SecureContinuityMode.DpapiCurrentUser)
        {
            try
            {
                var continuity = Workspace.State.Projects
                    .SelectMany(project => project.Tasks)
                    .Select(task => (task.Id, Continuity: new TaskContinuity(
                            TaskRuntimes.Find(task.Id)?.Queue.Snapshot() ?? [],
                            goals.GetValueOrDefault(task.Id))))
                    .Where(pair => pair.Continuity.Queue.Count > 0 ||
                                   pair.Continuity.Goal is not null ||
                                   !string.IsNullOrWhiteSpace(pair.Continuity.HandoffDraft))
                    .ToDictionary(pair => pair.Id, pair => pair.Continuity);
                await continuityStore.SaveAllAsync(continuity, CancellationToken.None);
            }
            catch (Exception error)
            {
                CrashDiagnostics.Record("Secure continuity shutdown save", error);
            }
        }

        lifetime.Cancel();
        if (healthMonitor is not null)
        {
            try
            {
                await healthMonitor;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Backend.DisposeAsync();
        foreach (var continuityGate in continuityGates.Values)
        {
            continuityGate.Dispose();
        }

        lifetime.Dispose();
        metadataGate.Dispose();
        threadIndexGate.Dispose();
        automationGate.Dispose();
        initializeGate.Dispose();
    }

    private async Task InitializeRuntimeStateAsync(CancellationToken cancellationToken)
    {
        hydratingContinuity = true;
        try
        {
            var continuityByTask = Workspace.State.SecureContinuityMode is SecureContinuityMode.DpapiCurrentUser
                ? await continuityStore.LoadAllAsync(cancellationToken)
                : new Dictionary<Guid, TaskContinuity>();
            foreach (var project in Workspace.State.Projects)
            {
                foreach (var task in project.Tasks)
                {
                    var session = RegisterRuntime(project.Id, task);
                    if (continuityByTask.TryGetValue(task.Id, out var continuity))
                    {
                        session.Queue.Replace(continuity.Queue);
                        goals[task.Id] = continuity.Goal;
                    }
                }
            }
        }
        finally
        {
            hydratingContinuity = false;
        }
    }

    private TaskRuntimeSession RegisterRuntime(Guid projectId, TaskRecord task)
    {
        var session = TaskRuntimes.GetOrCreate(projectId, task.Id, task.ThreadId);
        if (!continuityGates.ContainsKey(task.Id))
        {
            continuityGates[task.Id] = new SemaphoreSlim(1, 1);
            session.Queue.Changed += (_, _) =>
            {
                if (!hydratingContinuity)
                {
                    _ = SaveContinuityAsync(task.Id, lifetime.Token);
                }
            };
        }

        return session;
    }

    private async Task SaveContinuityAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (Workspace.State.SecureContinuityMode is not SecureContinuityMode.DpapiCurrentUser)
        {
            return;
        }

        var gate = continuityGates.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = TaskRuntimes.Find(taskId);
            await continuityStore.SaveTaskAsync(
                taskId,
                new TaskContinuity(runtime?.Queue.Snapshot() ?? [], goals.GetValueOrDefault(taskId)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LastError = $"Secure continuity could not be saved: {error.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Backend_StateChanged(object? sender, AgentBackendSnapshot state)
    {
        TaskRuntimes.HandleConnectionState(state);
        ConnectionState = state.State switch
        {
            AgentBackendState.Ready => CodexConnectionState.Ready,
            AgentBackendState.Recovering => CodexConnectionState.Recovering,
            AgentBackendState.Connecting => CodexConnectionState.Connecting,
            AgentBackendState.Failed => CodexConnectionState.Failed,
            _ => CodexConnectionState.NotStarted,
        };
        LastError = state.Error;
        Changed?.Invoke(this, EventArgs.Empty);
        if (initialized && state.State is AgentBackendState.Ready && state.Generation > 1)
        {
            _ = RecoverTrackedThreadsAsync(lifetime.Token);
        }
    }

    private async Task RefreshBackendMetadataAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        await metadataGate.WaitAsync(cancellationToken);
        try
        {
            Models = (await Backend.ListModelsAsync(cancellationToken)).Data
                .Where(model => !model.Hidden)
                .ToArray();
            if (Backend.Descriptor.Supports(AgentBackendCapabilities.AdaptiveAgents))
            {
                try
                {
                    AdaptiveThreadConfiguration = await adaptiveAgentStore.EnsureAsync(
                        AdaptiveAgentCatalog.Create(Models.Select(model => model.ToRoutingOption()).ToArray()),
                        cancellationToken);
                    AdaptiveRoutingError = null;
                }
                catch (Exception error)
                {
                    AdaptiveThreadConfiguration = AppServerThreadConfiguration.Empty;
                    AdaptiveRoutingError = error.Message;
                }
            }
            else
            {
                AdaptiveThreadConfiguration = AppServerThreadConfiguration.Empty;
                AdaptiveRoutingError = null;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) &&
                Backend.Descriptor.Supports(AgentBackendCapabilities.PermissionProfiles))
            {
                PermissionProfiles = (await Backend.ListPermissionProfilesAsync(
                    workingDirectory,
                    cancellationToken)).Data;
            }
            else
            {
                PermissionProfiles = [];
            }
        }
        finally
        {
            metadataGate.Release();
        }
    }

    private async Task<IReadOnlyList<CodexThread>> ListAllThreadsAsync(
        bool archived,
        CancellationToken cancellationToken)
    {
        var result = new List<CodexThread>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var response = await Backend.ListThreadsAsync(
                archived: archived,
                cursor: cursor,
                limit: 100,
                cancellationToken: cancellationToken);
            result.AddRange(response.Data);
            cursor = response.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor) && seenCursors.Add(cursor));

        return result;
    }

    private static CodexThreadIndexEntry CreateIndexEntry(CodexThread thread, bool archived)
    {
        var title = !string.IsNullOrWhiteSpace(thread.Name)
            ? thread.Name.Trim()
            : !string.IsNullOrWhiteSpace(thread.Preview)
                ? FirstLine(thread.Preview)
                : "Untitled task";
        return new CodexThreadIndexEntry(
            thread.Id,
            title,
            thread.Preview ?? string.Empty,
            Path.GetFullPath(thread.WorkingDirectory),
            DateTimeOffset.FromUnixTimeSeconds(thread.CreatedAt),
            DateTimeOffset.FromUnixTimeSeconds(thread.UpdatedAt),
            archived,
            null,
            null,
            null);
    }

    private void ReconcileThreadIndex()
    {
        var taskLookup = Workspace.State.Projects
            .SelectMany(project => project.Tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.ThreadId))
                .Select(task => (project, task)))
            .GroupBy(pair => pair.task.ThreadId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        ThreadIndex = ThreadIndex.Select(entry =>
        {
            if (!taskLookup.TryGetValue(entry.ThreadId, out var match))
            {
                return entry with { ProjectId = null, TaskId = null, ProjectName = null };
            }

            return entry with
            {
                ProjectId = match.project.Id,
                TaskId = match.task.Id,
                ProjectName = match.project.Name,
            };
        }).ToArray();
    }

    private static string FirstLine(string value)
    {
        var first = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "Untitled task";
        return first.Length <= 90 ? first : $"{first[..87]}…";
    }

    private async Task RecoverTrackedThreadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshBackendMetadataAsync(null, cancellationToken);
            foreach (var runtime in TaskRuntimes.SnapshotAll().Where(snapshot =>
                         !string.IsNullOrWhiteSpace(snapshot.ThreadId)))
            {
                var task = Workspace.GetTask(runtime.ProjectId, runtime.TaskId);
                try
                {
                    var response = await Backend.ResumeThreadAsync(
                        runtime.ThreadId!,
                        task.WorkingDirectory,
                        cancellationToken,
                        AdaptiveThreadConfiguration);
                    var active = response.Thread.Turns.LastOrDefault(turn => IsTurnActive(turn.Status));
                    var session = TaskRuntimes.GetOrCreate(runtime.ProjectId, runtime.TaskId, runtime.ThreadId);
                    session.AttachTurn(active?.Id);
                    session.SetState(active is null ? TaskRunState.Idle : TaskRunState.Running);
                }
                catch (Exception error)
                {
                    TaskRuntimes.Find(runtime.TaskId)?.SetState(TaskRunState.Disconnected, error.Message);
                }
            }
        }
        catch (Exception error)
        {
            LastError = error.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Backend_NotificationReceived(object? sender, AppServerEvent notification)
    {
        if (notification.Method.Equals("turn/started", StringComparison.Ordinal))
        {
            var startedTurnId = ReadString(notification.Parameters, "turn", "id")
                                ?? ReadString(notification.Parameters, "turnId");
            if (!string.IsNullOrWhiteSpace(startedTurnId))
            {
                turnStartedAt[startedTurnId] = DateTimeOffset.UtcNow;
            }
        }

        TaskRuntimes.HandleNotification(notification);
        if (!notification.Method.Equals("turn/completed", StringComparison.Ordinal))
        {
            return;
        }

        var threadId = ReadString(notification.Parameters, "threadId")
                       ?? ReadString(notification.Parameters, "conversationId");
        if (string.IsNullOrWhiteSpace(threadId) ||
            !activeAutomationRuns.TryRemove(threadId, out var runId))
        {
            return;
        }

        var turnId = ReadString(notification.Parameters, "turn", "id")
                     ?? ReadString(notification.Parameters, "turnId");
        var status = ReadString(notification.Parameters, "turn", "status")
                     ?? ReadString(notification.Parameters, "status");
        var error = status is "failed" or "error" ? "The scheduled Codex turn failed." : null;
        _ = CompleteAutomationRunAsync(runId, threadId, turnId, error, lifetime.Token);
    }

    private async Task CompleteAutomationRunAsync(
        Guid runId,
        string? threadId,
        string? turnId,
        string? error,
        CancellationToken cancellationToken)
    {
        var run = AutomationRuns.FirstOrDefault(candidate => candidate.Id == runId);
        if (run is null)
        {
            return;
        }

        string? response = null;
        if (error is null && !string.IsNullOrWhiteSpace(threadId))
        {
            try
            {
                var thread = (await Backend.ReadThreadAsync(threadId, cancellationToken)).Thread;
                var turn = !string.IsNullOrWhiteSpace(turnId)
                    ? thread.Turns.LastOrDefault(candidate => candidate.Id == turnId)
                    : thread.Turns.Count > 0 ? thread.Turns[^1] : null;
                response = turn?.Items
                    .Where(item => string.Equals(
                        ReadString(item, "type"),
                        "agentMessage",
                        StringComparison.Ordinal))
                    .Select(item => ReadString(item, "text"))
                    .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
            }
            catch (Exception readError)
            {
                error = $"The run completed, but its result could not be read: {readError.Message}";
            }
        }

        var classification = AutomationResultClassifier.Classify(run.Purpose, response, error);
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            AutomationRuns = AutomationRuns.Select(candidate => candidate.Id == runId
                ? candidate with
                {
                    CompletedAt = DateTimeOffset.UtcNow,
                    Status = error is null ? AutomationRunStatus.Completed : AutomationRunStatus.Failed,
                    NeedsAttention = classification.NeedsAttention,
                    IsRead = !classification.NeedsAttention,
                    Summary = classification.Summary,
                    Error = error,
                    ThreadId = threadId ?? candidate.ThreadId,
                    TurnId = turnId ?? candidate.TurnId,
                }
                : candidate).ToArray();
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await FinalizeWorkReceiptAsync(
                run.ProjectId,
                run.TaskId,
                turnId,
                error is null ? "completed" : "failed",
                isAutomated: true,
                startedAt: run.StartedAt,
                cancellationToken: cancellationToken);
        }
        catch (Exception receiptError)
        {
            LastError = $"The scheduled run completed, but its work receipt failed: {receiptError.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private Task SaveAutomationStateAsync(CancellationToken cancellationToken) =>
        automationStore.SaveStateAsync(new AutomationState(Automations, AutomationRuns), cancellationToken);

    private async Task MonitorTaskHealthAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            TaskRuntimes.UpdateQuietState(now);
            await RunDueAutomationsAsync(now, cancellationToken);
            foreach (var runtime in TaskRuntimes.SnapshotAll().Where(snapshot =>
                         snapshot.State is TaskRunState.Running &&
                         !string.IsNullOrWhiteSpace(snapshot.ThreadId) &&
                         now - snapshot.LastActivityAt >= TimeSpan.FromMinutes(1)))
            {
                if (lastHealthProbe.TryGetValue(runtime.TaskId, out var last) &&
                    now - last < TimeSpan.FromMinutes(1))
                {
                    continue;
                }

                lastHealthProbe[runtime.TaskId] = now;
                try
                {
                    await Backend.ReadThreadAsync(runtime.ThreadId!, cancellationToken);
                }
                catch (Exception error)
                {
                    TaskRuntimes.Find(runtime.TaskId)?.SetState(TaskRunState.Recovering, error.Message);
                }
            }
        }
    }

    private async Task RunDueAutomationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var definition in Automations.Where(candidate =>
                     candidate.IsEnabled && candidate.NextRunAt <= now).ToArray())
        {
            var runtime = TaskRuntimes.Find(definition.TaskId);
            if (runtime?.Snapshot.State is TaskRunState.Starting or TaskRunState.Running or
                TaskRunState.WaitingForApproval or TaskRunState.WaitingForInput or TaskRunState.Stopping)
            {
                continue;
            }

            string? error = null;
            try
            {
                await StartAutomationRunAsync(definition, cancellationToken);
            }
            catch (Exception failure)
            {
                error = failure.Message;
                TaskRuntimes.Find(definition.TaskId)?.SetState(TaskRunState.Failed, failure.Message);
            }

            await automationGate.WaitAsync(cancellationToken);
            try
            {
                Automations = Automations.Select(candidate => candidate.Id == definition.Id
                    ? candidate.Advance(now, error)
                    : candidate).ToArray();
                await SaveAutomationStateAsync(cancellationToken);
            }
            finally
            {
                automationGate.Release();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task StartAutomationRunAsync(
        ScheduledTaskDefinition automation,
        CancellationToken cancellationToken)
    {
        var runtime = TaskRuntimes.Find(automation.TaskId);
        if (runtime?.Snapshot.State is TaskRunState.Starting or TaskRunState.Running or
            TaskRunState.WaitingForApproval or TaskRunState.WaitingForInput or TaskRunState.Stopping)
        {
            throw new InvalidOperationException("The target task is already running.");
        }

        var run = new AutomationRunRecord(
            Guid.NewGuid(),
            automation.Id,
            automation.ProjectId,
            automation.TaskId,
            automation.Name,
            automation.Purpose,
            DateTimeOffset.UtcNow,
            null,
            AutomationRunStatus.Running,
            false,
            true,
            "Running\u2026");
        await automationGate.WaitAsync(cancellationToken);
        try
        {
            AutomationRuns = [run, .. AutomationRuns.Take(199)];
            await SaveAutomationStateAsync(cancellationToken);
        }
        finally
        {
            automationGate.Release();
        }

        try
        {
            var started = await StartAutomationTurnAsync(automation, cancellationToken);
            activeAutomationRuns[started.ThreadId] = run.Id;
            await automationGate.WaitAsync(cancellationToken);
            try
            {
                AutomationRuns = AutomationRuns.Select(candidate => candidate.Id == run.Id
                    ? candidate with { ThreadId = started.ThreadId, TurnId = started.TurnId }
                    : candidate).ToArray();
                await SaveAutomationStateAsync(cancellationToken);
            }
            finally
            {
                automationGate.Release();
            }
        }
        catch (Exception error)
        {
            await CompleteAutomationRunAsync(run.Id, null, null, error.Message, cancellationToken);
            throw;
        }
    }

    private async Task<(string ThreadId, string TurnId)> StartAutomationTurnAsync(
        ScheduledTaskDefinition automation,
        CancellationToken cancellationToken)
    {
        if (!Backend.Descriptor.Supports(AgentBackendCapabilities.UnattendedTurns))
        {
            throw new AgentBackendCapabilityException(
                Backend.Descriptor,
                AgentBackendCapabilities.UnattendedTurns);
        }

        var project = Workspace.GetProject(automation.ProjectId);
        var task = Workspace.GetTask(automation.ProjectId, automation.TaskId);
        var session = RegisterRuntime(project.Id, task);
        var specialSkills = Backend.Descriptor.Supports(AgentBackendCapabilities.Skills)
            ? SpecialSkillCatalog.Normalize(Workspace.State.EnabledSpecialSkills)
            : [];
        var route = Router.Decide(new RoutingRequest(
            automation.Prompt,
            task.WorkingDirectory,
            Models.Select(model => model.ToRoutingOption()).ToArray(),
            project.RoutingLock,
            task.RoutingLock,
            SpecialSkills: specialSkills,
            Policy: Workspace.State.RoutingPolicy));
        if (route.Permission is PermissionLevel.FullAccess)
        {
            route = route with
            {
                Permission = PermissionLevel.WorkspaceWrite,
                PermissionSource = RoutingSource.Auto,
                Reason = $"{route.Reason} Unattended scheduled runs are capped at workspace write.",
            };
        }
        var adaptive = CreateAdaptiveRoutingPlan(route, Workspace.State.AutoRoutingMode);
        var prompt = goals.GetValueOrDefault(task.Id) is { State: GoalRunState.Active } goal
            ? $"{automation.Prompt}\n\n<active_goal>\n{goal.ToPrompt()}\n</active_goal>"
            : automation.Prompt;
        session.SetRoute(route);
        session.SetState(TaskRunState.Starting);
        var threadId = task.ThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            var started = await Backend.StartThreadAsync(
                task.WorkingDirectory,
                route,
                route.ModelSource is not RoutingSource.Auto,
                cancellationToken,
                AdaptiveThreadConfiguration);
            threadId = started.Thread.Id;
            await Workspace.AttachThreadAsync(project.Id, task.Id, threadId, cancellationToken);
            session.AttachThread(threadId);
        }
        else
        {
            // Scheduled turns can run before their task page has ever been opened
            // in this process, so explicitly restore the native Codex thread.
            await Backend.ResumeThreadAsync(
                threadId,
                task.WorkingDirectory,
                cancellationToken,
                AdaptiveThreadConfiguration);
            session.AttachThread(threadId);
        }

        var turnSkills = ResolveTurnSkills(specialSkills, adaptive).ToList();
        if (automation.Purpose is ScheduledTaskPurpose.Monitor &&
            Backend.Descriptor.Supports(AgentBackendCapabilities.Skills))
        {
            turnSkills.Add(ResolveSkillReference(
                "scheduled-monitor-codex",
                Path.Combine("SpecialSkills", "ScheduledMonitorCodex", "SKILL.md"),
                "scheduled monitor"));
        }

        var turn = await Backend.StartTurnAsync(
            threadId,
            prompt,
            task.WorkingDirectory,
            route,
            cancellationToken,
            turnSkills,
            unattended: true);
        session.AttachTurn(turn.Turn.Id);
        session.SetState(TaskRunState.Running);
        return (threadId, turn.Turn.Id);
    }

    private static bool IsTurnActive(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.String)
        {
            return string.Equals(status.GetString(), "inProgress", StringComparison.OrdinalIgnoreCase);
        }

        return status.ValueKind == JsonValueKind.Object &&
               status.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), "inProgress", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string objectProperty, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectProperty, out var nested)
            ? ReadString(nested, property)
            : null;

    private static AppServerSkillReference ResolveSkillReference(
        string name,
        string relativePath,
        string displayName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The {displayName} specialization is missing from this app build.",
                path);
        }

        return new AppServerSkillReference(name, path);
    }
}
