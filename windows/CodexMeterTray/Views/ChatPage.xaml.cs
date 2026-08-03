using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexDecision.Core.AgentBackends;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.Git;
using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CodexMeterTray.Views;

public sealed class TimelineEntry : INotifyPropertyChanged
{
    private string text;

    public TimelineEntry(string role, string text, IReadOnlyList<FileChangeLine>? fileChanges = null)
    {
        Role = role;
        this.text = text;
        FileChanges = new ObservableCollection<FileChangeLine>(fileChanges ?? []);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Role { get; }

    public ObservableCollection<FileChangeLine> FileChanges { get; }

    public int FileChangeCount => FileChanges.Count;

    public Visibility UserVisibility => Role.StartsWith("YOU", StringComparison.Ordinal)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AssistantVisibility => Role is "CHANGES" || Role.StartsWith("YOU", StringComparison.Ordinal)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility FileChangesVisibility => Role is "CHANGES"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string Text
    {
        get => text;
        set
        {
            if (text == value)
            {
                return;
            }

            text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public void MergeFileChanges(IEnumerable<FileChangeLine> changes)
    {
        foreach (var change in changes)
        {
            var existing = FileChanges.FirstOrDefault(candidate =>
                string.Equals(
                    NormalizeDisplayPath(candidate.Path),
                    NormalizeDisplayPath(change.Path),
                    StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                FileChanges.Add(change);
            }
            else
            {
                existing.UpdateStats(change.Additions, change.Deletions);
            }
        }

        Text = FileChanges.Count == 1 ? "Edited 1 file" : $"Edited {FileChanges.Count} files";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileChangeCount)));
    }

    private static string NormalizeDisplayPath(string path) => path.Replace('\\', '/').Trim();
}

public sealed class ActivityEntry(string kind, string text)
{
    public string Kind { get; set; } = kind;

    public string Text { get; set; } = text;
}

public sealed class FileChangeLine : INotifyPropertyChanged
{
    private int additions;
    private int deletions;
    private bool isUndone;

    public FileChangeLine(string path, int additions, int deletions)
    {
        Path = path;
        this.additions = additions;
        this.deletions = deletions;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public int Additions => additions;

    public int Deletions => deletions;

    public string AdditionText => $"+{Additions}";

    public string DeletionText => $"-{Deletions}";

    public bool IsUndone
    {
        get => isUndone;
        set
        {
            if (isUndone == value)
            {
                return;
            }

            isUndone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUndone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUndo)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UndoLabel)));
        }
    }

    public bool CanUndo => !IsUndone;

    public string UndoLabel => IsUndone ? "Undone" : "Undo file";

    public string UndoAutomationName => $"Undo changes to {Path}";

    public void UpdateStats(int updatedAdditions, int updatedDeletions)
    {
        additions = updatedAdditions;
        deletions = updatedDeletions;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Additions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Deletions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdditionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeletionText)));
    }
}

public sealed record ModelChoice(
    string Label,
    string? ModelId,
    AutoRoutingMode? AutoMode = null);

public sealed record EffortChoice(string Label, string? Effort);

public sealed record PermissionChoice(string Label, PermissionLevel? Permission);

public sealed class QueuedFollowUpView(
    Guid id,
    string positionLabel,
    string prompt,
    string routeLabel,
    bool canMoveUp,
    bool canMoveDown)
{
    public Guid Id { get; set; } = id;

    public string PositionLabel { get; set; } = positionLabel;

    public string Prompt { get; set; } = prompt;

    public string RouteLabel { get; set; } = routeLabel;

    public bool CanMoveUp { get; set; } = canMoveUp;

    public bool CanMoveDown { get; set; } = canMoveDown;
}

public partial class ChatPage : Page
{
    private const double CompactComposerBreakpoint = 860;
    private const double StackedSelectorBreakpoint = 600;
    private const string VoiceNoteInstruction =
        "Transcribe the attached voice note faithfully. Preserve its spoken language and do not make any file changes.";
    private const string DefaultPromptPlaceholder = "Ask Codex to change, explain, or review something";

    private sealed record TurnLaunchContext(
        TaskRecord Task,
        RoutingDecision Route,
        IReadOnlyList<AppServerSkillReference> AppServerSkills,
        IReadOnlyList<AppServerAttachment> Attachments);

    private sealed record GoalPrompt(string DisplayPrompt, string PromptForCodex);

    private readonly AppServices services = ((App)Application.Current).Services;
    private readonly Guid projectId;
    private readonly Guid taskId;
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private readonly SemaphoreSlim turnActionGate = new(1, 1);
    private readonly DispatcherTimer environmentRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };
    private readonly TaskRuntimeSession runtime;
    private readonly FollowUpQueue followUpQueue;
    private TimelineEntry? streamingAssistantEntry;
    private TimelineEntry? activeFileChangeEntry;
    private RoutingDecision? lastDecision;
    private bool subscribed;
    private bool isPageLoaded;
    private bool queueSubscribed;
    private bool stopRequested;
    private bool composerActionBusy;
    private bool isPopulatingModelMode;
    private bool? contextPaneOverride;
    private MediaCapture? audioCapture;
    private StorageFile? activeRecording;
    private readonly HashSet<string> managedVoiceNotePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool isRecordingAudio;
    private bool captureNextPromptAsGoal;
    private bool environmentRefreshInFlight;
    private GitEnvironmentSnapshot? gitEnvironment;

    public ChatPage(Guid projectId, Guid taskId)
    {
        this.projectId = projectId;
        this.taskId = taskId;
        runtime = services.GetTaskRuntime(projectId, taskId);
        followUpQueue = runtime.Queue;
        InitializeComponent();
        environmentRefreshTimer.Tick += EnvironmentRefreshTimer_Tick;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        Loaded += ChatPage_Loaded;
        Unloaded += ChatPage_Unloaded;
    }

    public ObservableCollection<TimelineEntry> Timeline { get; } = [];

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public ObservableCollection<QueuedFollowUpView> QueuedFollowUps { get; } = [];

    public ObservableCollection<AppServerAttachment> Attachments { get; } = [];

    private void ToggleActivityPane_Click(object sender, RoutedEventArgs e)
    {
        var open = ActivityPane.Visibility is not Visibility.Visible;
        contextPaneOverride = open;
        ApplyContextPane(open);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyContextPane(contextPaneOverride ?? e.NewSize.Width >= 1040);
        ApplyComposerLayout(e.NewSize.Width);
    }

    private void ApplyComposerLayout(double width)
    {
        var compact = width < CompactComposerBreakpoint;
        var stackSelectors = width < StackedSelectorBreakpoint;
        Grid.SetRow(ComposerActionPanel, compact ? 1 : 0);
        Grid.SetColumn(ComposerActionPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(ComposerActionPanel, compact ? 2 : 1);
        ComposerActionPanel.HorizontalAlignment = HorizontalAlignment.Right;

        Grid.SetRow(EffortCombo, stackSelectors ? 1 : 0);
        Grid.SetColumn(EffortCombo, stackSelectors ? 0 : 2);
        Grid.SetRow(PermissionCombo, stackSelectors ? 1 : 0);
        Grid.SetColumn(PermissionCombo, stackSelectors ? 1 : 3);
    }

    private void ApplyContextPane(bool open)
    {
        ContextColumn.Width = open ? new GridLength(312) : new GridLength(0);
        ActivityPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ActivityPaneButton.Content = open ? "Hide details" : "Details";
        AutomationProperties.SetName(
            ActivityPaneButton,
            open ? "Hide task context pane" : "Show task context pane");
    }

    private string? currentThreadId
    {
        get => runtime.Snapshot.ThreadId;
        set => runtime.AttachThread(value);
    }

    private string? currentTurnId
    {
        get => runtime.Snapshot.TurnId;
        set => runtime.AttachTurn(value);
    }

    private bool turnIsRunning => runtime.Snapshot.State is
        TaskRunState.Starting or TaskRunState.Running or TaskRunState.WaitingForApproval or
        TaskRunState.WaitingForInput or TaskRunState.Stopping or TaskRunState.Recovering;

    private bool queuePaused
    {
        get => runtime.Snapshot.QueuePaused;
        set => runtime.SetQueuePaused(value);
    }

    private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
    {
        isPageLoaded = true;
        ApplyContextPane(contextPaneOverride ?? RootGrid.ActualWidth >= 1040);
        Subscribe();
        SubscribeQueue();
        runtime.Changed += Runtime_Changed;
        try
        {
            var task = services.Workspace.GetTask(projectId, taskId);
            await services.InitializeAsync(task.WorkingDirectory);
            PopulateSelectors();
            RenderTask(task);
            RenderLock();
            RefreshQueuedFollowUps();
            RenderGoal();
            RenderRuntime(runtime.Snapshot);
            RenderWorkReceipt();
            await RefreshEnvironmentAsync();
            environmentRefreshTimer.Start();

            if (services.TakeDraftPrompt(taskId) is { } draft)
            {
                PromptBox.Text = draft;
            }

            if (!string.IsNullOrWhiteSpace(task.ThreadId))
            {
                currentThreadId = task.ThreadId;
                var response = await services.Backend.ResumeThreadAsync(
                    task.ThreadId,
                    task.WorkingDirectory,
                    configuration: services.AdaptiveThreadConfiguration);
                RenderHistory(response.Thread);
                foreach (var approval in services.GetPendingApprovals(task.ThreadId))
                {
                    await HandleApprovalAsync(approval);
                }
            }

            if (!turnIsRunning && followUpQueue.Peek() is not null && !queuePaused)
            {
                await DispatchNextQueuedAsync();
            }
        }
        catch (Exception error)
        {
            ShowRouteError(error.Message);
        }
    }

    private void ChatPage_Unloaded(object sender, RoutedEventArgs e)
    {
        isPageLoaded = false;
        environmentRefreshTimer.Stop();
        if (isRecordingAudio)
        {
            _ = DiscardAudioRecordingAsync();
        }
        else if (audioCapture is not null)
        {
            _ = DisposeAudioCaptureAsync();
        }
        runtime.Changed -= Runtime_Changed;
        UnsubscribeQueue();
        if (turnIsRunning)
        {
            return;
        }

        Unsubscribe();
    }

    private void Runtime_Changed(object? sender, TaskRuntimeSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => RenderRuntime(snapshot));
    }

    private void RenderRuntime(TaskRuntimeSnapshot snapshot)
    {
        if (ContextMeterPanel is null)
        {
            return;
        }

        var runtimeStatus = snapshot.IsQuiet
            ? $"{snapshot.State} / quiet"
            : snapshot.State.ToString();
        var contextStatus = snapshot.Context.UsedPercent is { } percent
            ? $"Context {percent:0}% / {snapshot.Context.Pressure}"
            : snapshot.Context.TotalTokens > 0
                ? $"Context {snapshot.Context.TotalTokens:N0} tokens"
                : "Context pending";
        var description = $"{contextStatus} · {runtimeStatus}";
        ToolTipService.SetToolTip(ContextMeterPanel, description);
        AutomationProperties.SetName(ContextMeterPanel, description);
        ContextMeterGlyph.Text = snapshot.Context.UsedPercent is { } usedPercent
            ? $"{usedPercent:0}"
            : snapshot.Context.IsCompacting ? "…" : "i";
        ContextProgressRing.IsActive = snapshot.Context.IsCompacting || !snapshot.IsQuiet;
        ContextProgressRing.Visibility = ContextProgressRing.IsActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateComposerState();
    }

    private void SubscribeQueue()
    {
        if (queueSubscribed)
        {
            return;
        }

        followUpQueue.Changed += FollowUpQueue_Changed;
        queueSubscribed = true;
    }

    private void UnsubscribeQueue()
    {
        if (!queueSubscribed)
        {
            return;
        }

        followUpQueue.Changed -= FollowUpQueue_Changed;
        queueSubscribed = false;
    }

    private void FollowUpQueue_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshQueuedFollowUps);
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        services.Backend.NotificationReceived -= Codex_NotificationReceived;
        services.Backend.ApprovalRequested -= Codex_ApprovalRequested;
        subscribed = false;
    }

    private void Subscribe()
    {
        if (subscribed)
        {
            return;
        }

        services.Backend.NotificationReceived += Codex_NotificationReceived;
        services.Backend.ApprovalRequested += Codex_ApprovalRequested;
        subscribed = true;
    }

    private void PopulateSelectors()
    {
        isPopulatingModelMode = true;
        ModelCombo.ItemsSource = new[]
            {
                new ModelChoice("Full Auto / Adaptive", null, AutoRoutingMode.Adaptive),
                new ModelChoice("Full Auto / One model", null, AutoRoutingMode.SingleModel),
            }
            .Concat(services.Models.Select(model => new ModelChoice(model.DisplayName, model.Model)))
            .ToArray();
        SelectPersistedAutoRoutingMode();
        isPopulatingModelMode = false;

        EffortCombo.ItemsSource = new[]
        {
            new EffortChoice("Auto effort", null),
            new EffortChoice("Low", "low"),
            new EffortChoice("Medium", "medium"),
            new EffortChoice("High", "high"),
            new EffortChoice("Extra high", "xhigh"),
        };
        EffortCombo.SelectedIndex = 0;

        PermissionCombo.ItemsSource = new[]
        {
            new PermissionChoice("Auto safe", null),
            new PermissionChoice("Read-only", PermissionLevel.ReadOnly),
            new PermissionChoice("Workspace-write", PermissionLevel.WorkspaceWrite),
            new PermissionChoice("Full access", PermissionLevel.FullAccess),
        };
        PermissionCombo.SelectedIndex = 0;

        UpdateComposerState();
        RenderDecisionContext();
    }

    private void RenderTask(TaskRecord task)
    {
        TaskTitleText.Text = task.Title;
        WorkingDirectoryText.Text = task.WorkingDirectory;
        ContextLocationText.Text = task.ExecutionLocation is ExecutionLocation.ManagedWorktree
            ? "Managed worktree"
            : "Local";
        ContextLocationDetailText.Text = Path.GetFileName(task.WorkingDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        ContextBranchText.Text = "Reading branch…";
        ContextBranchDetailText.Text = "Checking upstream";
        ContextChangesText.Text = "Checking working tree…";
        ContextAdditionText.Text = string.Empty;
        ContextDeletionText.Text = string.Empty;
        CommitPushDetailText.Text = "Checking repository";
    }

    private void RenderWorkReceipt()
    {
        if (WorkReceiptButton is null)
        {
            return;
        }

        var policy = services.Workspace.GetProject(projectId).VerificationPolicy ?? VerificationPolicy.Disabled;
        var receipt = services.GetLatestWorkReceipt(taskId);
        RerunVerificationButton.IsEnabled = policy.IsEnabled && receipt is not null && !turnIsRunning;
        WorkReceiptButton.IsEnabled = receipt is not null;
        if (receipt is null)
        {
            WorkReceiptStatusIcon.Glyph = policy.IsEnabled ? "\uE73E" : "\uE9D9";
            WorkReceiptStatusText.Text = policy.IsEnabled ? "Waiting for first receipt" : "Verification gate off";
            WorkReceiptDetailText.Text = policy.IsEnabled
                ? DescribeCheckCount(policy.Steps.Count)
                : "Configure deterministic checks";
            return;
        }

        WorkReceiptStatusIcon.Glyph = receipt.GateStatus switch
        {
            VerificationGateStatus.Passed => "\uE73E",
            VerificationGateStatus.Failed or VerificationGateStatus.Error => "\uEA39",
            VerificationGateStatus.Skipped => "\uE711",
            _ => "\uE9D9",
        };
        WorkReceiptStatusText.Text = receipt.GateStatus switch
        {
            VerificationGateStatus.Passed => $"Verified · {receipt.Verification.Count}/{receipt.Verification.Count} checks",
            VerificationGateStatus.Failed => $"Verification failed · {receipt.Verification.Count(result => result.Status is VerificationStepStatus.Passed)}/{receipt.Verification.Count} passed",
            VerificationGateStatus.Error => "Verification needs attention",
            VerificationGateStatus.Skipped => "Turn ended · checks not run",
            _ => "Receipt recorded · gate off",
        };
        var files = receipt.ChangedFileCount == 1 ? "1 file" : $"{receipt.ChangedFileCount} files";
        WorkReceiptDetailText.Text = $"{files} · +{receipt.Additions} −{receipt.Deletions} · {DescribeAge(receipt.CompletedAt)}";
        ToolTipService.SetToolTip(
            WorkReceiptButton,
            $"{WorkReceiptStatusText.Text}. {files}, {receipt.ContextTokensAtCompletion:N0} context tokens at completion.");
    }

    private static string DescribeCheckCount(int count) => count == 1 ? "1 check after each turn" : $"{count} checks after each turn";

    private static string DescribeAge(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h ago";
        }
        return value.ToLocalTime().ToString("d MMM");
    }

    private async void ConfigureVerificationGateButton_Click(object sender, RoutedEventArgs e)
    {
        var current = services.Workspace.GetProject(projectId).VerificationPolicy ?? VerificationPolicy.Disabled;
        var enabled = new ToggleSwitch
        {
            Header = "Verification gate",
            OffContent = "Off",
            OnContent = "Run after every completed turn",
            IsOn = current.IsEnabled,
        };
        var commands = new TextBox
        {
            Header = "Commands (one per line)",
            PlaceholderText = "dotnet test\nnpm.cmd test",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 126,
            Text = string.Join(Environment.NewLine, current.Steps.Select(step => step.Command)),
        };
        var timeout = new NumberBox
        {
            Header = "Timeout per command (seconds)",
            Minimum = 5,
            Maximum = 1800,
            SmallChange = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = current.Steps.FirstOrDefault()?.TimeoutSeconds ?? 300,
        };
        var pauseQueue = new CheckBox
        {
            Content = "Pause queued work when verification fails",
            IsChecked = current.PauseQueueOnFailure,
        };
        var protectGit = new CheckBox
        {
            Content = "Require a current passing receipt before commit or push",
            IsChecked = current.RequirePassingReceiptBeforeGit,
        };
        var warning = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Project commands",
            Message = "Runs locally after successful turns using the project's selected profile.",
        };
        var panel = new StackPanel { Width = 540, Spacing = 12 };
        panel.Children.Add(enabled);
        panel.Children.Add(commands);
        panel.Children.Add(timeout);
        panel.Children.Add(pauseQueue);
        panel.Children.Add(protectGit);
        panel.Children.Add(warning);
        var dialog = CreateDialog("Verification gate", panel, "Save", "Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var timeoutSeconds = double.IsNaN(timeout.Value) ? 300 : (int)timeout.Value;
            var existingByCommand = current.Steps.ToDictionary(step => step.Command, StringComparer.OrdinalIgnoreCase);
            var steps = commands.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(command => command.Trim())
                .Where(command => command.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(command => existingByCommand.TryGetValue(command, out var existing)
                    ? existing with { TimeoutSeconds = timeoutSeconds }
                    : new VerificationStepDefinition(
                        Guid.NewGuid(),
                        command.Length <= 64 ? command : $"{command[..61]}…",
                        command,
                        timeoutSeconds))
                .ToArray();
            await services.SetVerificationPolicyAsync(
                projectId,
                new VerificationPolicy(
                    enabled.IsOn,
                    steps,
                    pauseQueue.IsChecked is true,
                    protectGit.IsChecked is true));
            RenderWorkReceipt();
            ShowEnvironmentMessage(enabled.IsOn
                ? $"Verification gate enabled with {DescribeCheckCount(steps.Length)}."
                : "Verification gate disabled. Work receipts will still record turn evidence.");
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void WorkReceiptButton_Click(object sender, RoutedEventArgs e)
    {
        var receipt = services.GetLatestWorkReceipt(taskId);
        if (receipt is null)
        {
            return;
        }

        await ShowWorkReceiptAsync(receipt);
    }

    private async void RerunVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var receipt = await RunVerificationWithUiAsync(resumeQueueOnPass: true);
            if (!receipt.Passed)
            {
                await ShowWorkReceiptAsync(receipt);
            }
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async Task<WorkReceipt> RunVerificationWithUiAsync(bool resumeQueueOnPass)
    {
        WorkReceiptStatusText.Text = "Running verification…";
        WorkReceiptDetailText.Text = "Commands are running in the project environment";
        RerunVerificationButton.IsEnabled = false;
        WorkReceiptButton.IsEnabled = false;
        try
        {
            var receipt = services.GetLatestWorkReceipt(taskId) is null
                ? await services.FinalizeWorkReceiptAsync(projectId, taskId, null, "completed")
                : await services.RerunVerificationAsync(projectId, taskId);
            RenderWorkReceipt();
            if (receipt.Passed)
            {
                ShowEnvironmentMessage("Verification passed. The work receipt is current.");
                if (resumeQueueOnPass && queuePaused && followUpQueue.Peek() is not null && !turnIsRunning)
                {
                    queuePaused = false;
                    UpdateQueueUi();
                    if (isPageLoaded)
                    {
                        await DispatchNextQueuedAsync();
                    }
                }
            }
            else
            {
                ShowQueueMessage("Verification did not pass. Open the work receipt for command output.", InfoBarSeverity.Warning);
            }
            return receipt;
        }
        finally
        {
            RenderWorkReceipt();
        }
    }

    private async Task<bool> EnsureVerificationGatePassedAsync(GitEnvironmentSnapshot environment)
    {
        var policy = services.Workspace.GetProject(projectId).VerificationPolicy ?? VerificationPolicy.Disabled;
        if (!policy.IsEnabled || !policy.RequirePassingReceiptBeforeGit)
        {
            return true;
        }

        var latest = services.GetLatestWorkReceipt(taskId);
        if (latest?.Passed is true && latest.Matches(environment))
        {
            return true;
        }

        var reason = latest is null
            ? "This project requires a passing work receipt before Git actions, and no receipt exists yet."
            : latest.Passed
                ? "The working tree changed after the last passing receipt."
                : "The latest verification gate did not pass.";
        if (await ShowConfirmDialogAsync(
                "Run verification before Git?",
                $"{reason}\n\nRun the configured project checks now?",
                "Run verification",
                "Cancel") is not ContentDialogResult.Primary)
        {
            return false;
        }

        var receipt = await RunVerificationWithUiAsync(resumeQueueOnPass: false);
        if (receipt.Passed)
        {
            var current = await services.Worktrees.GetEnvironmentAsync(
                services.Workspace.GetTask(projectId, taskId).WorkingDirectory);
            if (receipt.Matches(current))
            {
                return true;
            }

            ShowQueueMessage(
                "The working tree changed while verification was running. Run the gate again before the Git action.",
                InfoBarSeverity.Warning);
            return false;
        }

        await ShowWorkReceiptAsync(receipt);
        return false;
    }

    private async Task ShowWorkReceiptAsync(WorkReceipt receipt)
    {
        var content = BuildWorkReceiptContent(receipt);
        var policy = services.Workspace.GetProject(projectId).VerificationPolicy ?? VerificationPolicy.Disabled;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Work receipt",
            Content = content,
            PrimaryButtonText = "Export",
            SecondaryButtonText = policy.IsEnabled ? "Run again" : string.Empty,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary)
        {
            await ExportWorkReceiptAsync(receipt);
        }
        else if (result is ContentDialogResult.Secondary)
        {
            await RunVerificationWithUiAsync(resumeQueueOnPass: true);
        }
    }

    private UIElement BuildWorkReceiptContent(WorkReceipt receipt)
    {
        var panel = new StackPanel { Width = 560, Spacing = 14 };
        var status = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = receipt.GateStatus switch
            {
                VerificationGateStatus.Passed => "Verification passed",
                VerificationGateStatus.Failed => "Verification failed",
                VerificationGateStatus.Error => "Verification needs attention",
                VerificationGateStatus.Skipped => "Turn evidence recorded · checks not run",
                _ => "Turn evidence recorded",
            },
        };
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppMutedBrush"],
            Text = $"{receipt.CompletedAt.ToLocalTime():g} · {(receipt.IsAutomated ? "Scheduled run" : "Interactive turn")}",
        });

        var facts = new Grid { ColumnSpacing = 22, RowSpacing = 6 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddReceiptFact(facts, 0, 0, "Model", receipt.ModelId ?? "Not reported");
        AddReceiptFact(facts, 0, 1, "Permission", receipt.Permission?.ToString() ?? "Not reported");
        AddReceiptFact(facts, 1, 0, "Git", $"{receipt.BranchName ?? "No branch"} · {receipt.ChangedFileCount} files");
        AddReceiptFact(facts, 1, 1, "Diff", $"+{receipt.Additions} −{receipt.Deletions}");
        AddReceiptFact(facts, 2, 0, "Context at completion", $"{receipt.ContextTokensAtCompletion:N0} tokens");
        AddReceiptFact(facts, 2, 1, "Duration", DescribeDuration(receipt.CompletedAt - receipt.StartedAt));
        panel.Children.Add(facts);

        if (receipt.Verification.Count > 0)
        {
            panel.Children.Add(new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = "Verification" });
            foreach (var step in receipt.Verification)
            {
                var body = new StackPanel { Spacing = 6 };
                body.Children.Add(new TextBlock
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    Text = step.Command,
                    TextWrapping = TextWrapping.Wrap,
                });
                body.Children.Add(new TextBox
                {
                    MinHeight = 52,
                    MaxHeight = 170,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                    FontSize = 10,
                    Text = string.IsNullOrWhiteSpace(step.Output) ? "No command output." : step.Output,
                    TextWrapping = TextWrapping.NoWrap,
                });
                panel.Children.Add(new Expander
                {
                    Header = $"{DescribeStepStatus(step.Status)} · {step.Name} · {DescribeDuration(step.Duration)}",
                    Content = body,
                    IsExpanded = step.Status is not VerificationStepStatus.Passed,
                });
            }
        }

        panel.Children.Add(new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = "Deterministic risk flags" });
        panel.Children.Add(new TextBlock
        {
            Text = receipt.RiskFlags.Count == 0
                ? "No configured risk flags were triggered. This is evidence, not a security guarantee."
                : string.Join(Environment.NewLine, receipt.RiskFlags.Select(risk => $"• {risk}")),
            TextWrapping = TextWrapping.Wrap,
        });
        return new ScrollViewer
        {
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
    }

    private static void AddReceiptFact(Grid grid, int row, int column, string label, string value)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var text = new TextBlock
        {
            Text = $"{label}\n{value}",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, column);
        grid.Children.Add(text);
    }

    private static string DescribeStepStatus(VerificationStepStatus status) => status switch
    {
        VerificationStepStatus.Passed => "Passed",
        VerificationStepStatus.Failed => "Failed",
        VerificationStepStatus.TimedOut => "Timed out",
        _ => "Error",
    };

    private static string DescribeDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
        : $"{Math.Max(0, duration.TotalSeconds):0.0}s";

    private async Task ExportWorkReceiptAsync(WorkReceipt receipt)
    {
        var window = ((App)Application.Current).MainWindow;
        if (window is null)
        {
            return;
        }

        var task = services.Workspace.GetTask(projectId, taskId);
        var picker = new FileSavePicker { SuggestedFileName = $"{task.Title}-work-receipt" };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await File.WriteAllTextAsync(file.Path, BuildWorkReceiptMarkdown(receipt, task.Title));
        ShowEnvironmentMessage($"Work receipt exported to {file.Path}.");
    }

    private static string BuildWorkReceiptMarkdown(WorkReceipt receipt, string taskTitle)
    {
        var text = new StringBuilder()
            .AppendLine($"# Work receipt — {taskTitle}")
            .AppendLine()
            .AppendLine($"- Completed: {receipt.CompletedAt:u}")
            .AppendLine($"- Turn status: {receipt.TurnStatus}")
            .AppendLine($"- Verification gate: {receipt.GateStatus}")
            .AppendLine($"- Model: {receipt.ModelId ?? "Not reported"}")
            .AppendLine($"- Reasoning effort: {receipt.ReasoningEffort ?? "Not reported"}")
            .AppendLine($"- Permission: {receipt.Permission?.ToString() ?? "Not reported"}")
            .AppendLine($"- Context at completion: {receipt.ContextTokensAtCompletion:N0} tokens")
            .AppendLine($"- Git: {receipt.BranchName ?? "No branch"}, {receipt.ChangedFileCount} files, +{receipt.Additions} -{receipt.Deletions}")
            .AppendLine();
        if (receipt.Verification.Count > 0)
        {
            text.AppendLine("## Verification").AppendLine();
            foreach (var step in receipt.Verification)
            {
                text.AppendLine($"### {DescribeStepStatus(step.Status)} — {step.Name}")
                    .AppendLine()
                    .AppendLine($"Command: `{step.Command.Replace("`", "\\`")}`")
                    .AppendLine()
                    .AppendLine("````text")
                    .AppendLine(string.IsNullOrWhiteSpace(step.Output) ? "No command output." : step.Output)
                    .AppendLine("````")
                    .AppendLine();
            }
        }
        text.AppendLine("## Deterministic risk flags").AppendLine();
        if (receipt.RiskFlags.Count == 0)
        {
            text.AppendLine("No configured risk flags were triggered. This is evidence, not a security guarantee.");
        }
        else
        {
            foreach (var risk in receipt.RiskFlags)
            {
                text.AppendLine($"- {risk}");
            }
        }
        return text.ToString();
    }

    private async void EnvironmentRefreshTimer_Tick(object? sender, object e) =>
        await RefreshEnvironmentAsync();

    private async void RefreshEnvironmentButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshEnvironmentAsync(showErrors: true);

    private async Task RefreshEnvironmentAsync(bool showErrors = false)
    {
        if (environmentRefreshInFlight || !isPageLoaded)
        {
            return;
        }

        environmentRefreshInFlight = true;
        EnvironmentLoadingRing.IsActive = true;
        EnvironmentLoadingRing.Visibility = Visibility.Visible;
        RefreshEnvironmentButton.IsEnabled = false;
        try
        {
            var task = services.Workspace.GetTask(projectId, taskId);
            var environment = await services.Worktrees.GetEnvironmentAsync(task.WorkingDirectory);
            gitEnvironment = environment;

            ContextChangesText.Text = environment.IsClean
                ? "Working tree clean"
                : environment.Files.Count == 1
                    ? "1 changed file"
                    : $"{environment.Files.Count} changed files";
            ContextAdditionText.Text = $"+{environment.TotalAdditions}";
            ContextDeletionText.Text = $"-{environment.TotalDeletions}";
            ContextLocationDetailText.Text = Path.GetFileName(environment.RepositoryRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            ToolTipService.SetToolTip(ContextLocationDetailText, environment.RepositoryRoot);

            ContextBranchText.Text = string.IsNullOrWhiteSpace(environment.BranchName)
                ? "Detached HEAD"
                : environment.BranchName;
            ContextBranchDetailText.Text = BuildBranchDetail(environment);
            CommitPushDetailText.Text = BuildCommitPushDetail(environment);
            ChangesButton.IsEnabled = true;
            BranchButton.IsEnabled = true;
            CommitPushButton.IsEnabled = true;
            CompareBranchButton.IsEnabled = true;
        }
        catch (Exception error) when (error is GitCommandException or DirectoryNotFoundException)
        {
            gitEnvironment = null;
            ContextChangesText.Text = "Not a Git repository";
            ContextAdditionText.Text = string.Empty;
            ContextDeletionText.Text = string.Empty;
            ContextBranchText.Text = "No branch";
            ContextBranchDetailText.Text = "Git is unavailable for this folder";
            CommitPushDetailText.Text = "Repository required";
            ChangesButton.IsEnabled = false;
            BranchButton.IsEnabled = false;
            CommitPushButton.IsEnabled = false;
            CompareBranchButton.IsEnabled = false;
            if (showErrors)
            {
                ShowRouteError(FriendlyError(error));
            }
        }
        catch (Exception error)
        {
            if (showErrors)
            {
                ShowRouteError(FriendlyError(error));
            }
        }
        finally
        {
            environmentRefreshInFlight = false;
            EnvironmentLoadingRing.IsActive = false;
            EnvironmentLoadingRing.Visibility = Visibility.Collapsed;
            RefreshEnvironmentButton.IsEnabled = true;
        }
    }

    private static string BuildBranchDetail(GitEnvironmentSnapshot environment)
    {
        if (string.IsNullOrWhiteSpace(environment.BranchName))
        {
            return "Create a branch to commit or push";
        }

        if (string.IsNullOrWhiteSpace(environment.UpstreamBranch))
        {
            return "Not published";
        }

        var divergence = new List<string>();
        if (environment.AheadBy > 0)
        {
            divergence.Add($"{environment.AheadBy} ahead");
        }

        if (environment.BehindBy > 0)
        {
            divergence.Add($"{environment.BehindBy} behind");
        }

        divergence.Add(environment.UpstreamBranch);
        return string.Join(" · ", divergence);
    }

    private static string BuildCommitPushDetail(GitEnvironmentSnapshot environment)
    {
        if (!environment.IsClean)
        {
            return environment.Files.Count == 1
                ? "Commit 1 changed file"
                : $"Commit {environment.Files.Count} changed files";
        }

        if (string.IsNullOrWhiteSpace(environment.BranchName))
        {
            return "Create a branch first";
        }

        if (string.IsNullOrWhiteSpace(environment.UpstreamBranch))
        {
            return "Publish branch to origin";
        }

        return environment.AheadBy > 0
            ? $"Push {environment.AheadBy} commit{(environment.AheadBy == 1 ? string.Empty : "s")}"
            : "Working tree is clean";
    }

    private void RenderLock()
    {
        var project = services.Workspace.GetProject(projectId);
        var task = services.Workspace.GetTask(projectId, taskId);
        LockButton.Content = task.RoutingLock is not null
            ? "Task lock"
            : project.RoutingLock is not null
                ? "Project lock"
                : "No lock";
        RenderDecisionContext();
    }

    private void RenderDecisionContext()
    {
        if (ContextRouteText is null || ModelCombo is null)
        {
            return;
        }

        var route = (ModelCombo.SelectedItem as ModelChoice)?.Label ?? "Full Auto / Adaptive";
        var lockLabel = LockButton?.Content?.ToString();
        ContextRouteText.Text = string.IsNullOrWhiteSpace(lockLabel) || lockLabel == "No lock"
            ? route
            : $"{route} · {lockLabel}";
        var skills = ActiveSpecialSkills();
        ContextSkillsText.Text = skills.Count == 0
            ? "Special skills off"
            : $"Skills: {DescribeSkills(skills)}";
    }

    private void RenderHistory(CodexThread thread)
    {
        Timeline.Clear();
        Activity.Clear();
        activeFileChangeEntry = null;
        foreach (var turn in thread.Turns)
        {
            activeFileChangeEntry = null;
            foreach (var item in turn.Items)
            {
                RenderCompletedItem(item, isHistorical: true);
            }
        }
        ApplyPersistedFileUndoState();

        var activeTurn = thread.Turns.LastOrDefault(turn =>
            string.Equals(ReadElementString(turn.Status), "inProgress", StringComparison.OrdinalIgnoreCase));
        currentTurnId = activeTurn?.Id;
        SetTurnRunning(activeTurn is not null);
        ScrollToLatest();
    }

    private void ApplyPersistedFileUndoState()
    {
        var checkpoint = services.Workspace.GetTask(projectId, taskId).LastCheckpoint;
        if (checkpoint is null || activeFileChangeEntry is null)
        {
            return;
        }

        var undonePaths = checkpoint.IndividuallyUndonePaths ?? [];
        foreach (var file in activeFileChangeEntry.FileChanges)
        {
            file.IsUndone = checkpoint.IsUndone || undonePaths.Any(path =>
                string.Equals(
                    NormalizeCheckpointDisplayPath(checkpoint.RepositoryRoot, path),
                    NormalizeCheckpointDisplayPath(checkpoint.RepositoryRoot, file.Path),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string NormalizeCheckpointDisplayPath(string repositoryRoot, string path)
    {
        try
        {
            var relative = System.IO.Path.IsPathRooted(path)
                ? System.IO.Path.GetRelativePath(repositoryRoot, System.IO.Path.GetFullPath(path))
                : path;
            return relative.Replace('\\', '/').Trim();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('\\', '/').Trim();
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    private async void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (e.Key == VirtualKey.Enter && control.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await turnActionGate.WaitAsync();
        try
        {
            composerActionBusy = true;
            UpdateComposerState();
            var goalPrompt = await PrepareGoalPromptAsync(prompt);
            prompt = goalPrompt.PromptForCodex;
            if (turnIsRunning)
            {
                await QueuePromptAsync(prompt);
                return;
            }

            try
            {
                var specialSkills = ActiveSpecialSkills();
                var oneTurn = await BuildSelectedOverrideAsync();
                if (oneTurn is null && SelectedPermission() is PermissionLevel.FullAccess)
                {
                    return;
                }

                var context = PrepareTurn(
                    prompt,
                    oneTurn,
                    specialSkills,
                    SelectedAutoRoutingMode());

                activeFileChangeEntry = null;
                Timeline.Add(new TimelineEntry(UserRole("YOU", specialSkills), goalPrompt.DisplayPrompt));
                PromptBox.Text = string.Empty;
                SetTurnRunning(true);

                await StartCodexTurnAsync(prompt, context);
                ResetOneTurnSelectors();
                ScrollToLatest();
            }
            catch (Exception error)
            {
                if (string.IsNullOrWhiteSpace(PromptBox.Text))
                {
                    PromptBox.Text = prompt;
                }
                SetTurnRunning(false);
                ShowRouteError(FriendlyError(error));
            }
        }
        finally
        {
            composerActionBusy = false;
            UpdateComposerState();
            turnActionGate.Release();
        }
    }

    private async Task<bool> QueuePromptAsync(
        string prompt,
        IReadOnlyList<SpecialSkillId>? specialSkills = null,
        AutoRoutingMode? autoRoutingMode = null)
    {
        var oneTurn = await BuildSelectedOverrideAsync();
        if (oneTurn is null && SelectedPermission() is PermissionLevel.FullAccess)
        {
            return false;
        }

        var capturedSkills = SpecialSkillCatalog.Normalize(specialSkills ?? ActiveSpecialSkills());
        followUpQueue.Enqueue(
            prompt,
            oneTurn,
            capturedSkills,
            autoRoutingMode ?? SelectedAutoRoutingMode(),
            Attachments);
        Attachments.Clear();
        PromptBox.Text = string.Empty;
        ResetOneTurnSelectors();
        queuePaused = false;
        return true;
    }

    private async Task<bool> SteerPromptAsync(QueuedFollowUp item)
    {
        if (!services.Backend.Descriptor.Supports(AgentBackendCapabilities.TurnSteering))
        {
            ShowRouteError(
                $"{services.Backend.Descriptor.DisplayName} does not support steering. The message is still queued for the next turn.");
            return false;
        }

        var capturedSkills = SpecialSkillCatalog.Normalize(item.SpecialSkills);
        var threadId = currentThreadId;
        var turnId = currentTurnId;
        if (threadId is null || turnId is null)
        {
            ShowRouteError("There is no active turn to steer. The message is still queued for the next turn.");
            return false;
        }

        try
        {
            var response = await services.Backend.SteerTurnAsync(
                threadId,
                turnId,
                WithActiveGoal(item.Prompt),
                skills: services.Backend.Descriptor.Supports(AgentBackendCapabilities.Skills)
                    ? AppServices.ResolveSpecialSkills(capturedSkills)
                    : [],
                attachments: services.Backend.Descriptor.Supports(AgentBackendCapabilities.Attachments)
                    ? item.Attachments ?? []
                    : []);
            if (!string.Equals(response.TurnId, turnId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex acknowledged steering for a different active turn.");
            }

            Timeline.Add(new TimelineEntry(UserRole("YOU / STEER", capturedSkills), item.Prompt));
            Activity.Add(new ActivityEntry(
                "STEER",
                capturedSkills.Count > 0
                    ? $"Added follow-up instructions with {DescribeSkills(capturedSkills)} to the active turn."
                    : "Added follow-up instructions to the active turn."));
            ScrollToLatest();
            return true;
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
            return false;
        }
    }

    private async Task<bool> StartPromptTurnAsync(QueuedFollowUp queuedItem)
    {
        var prompt = queuedItem.Prompt;
        try
        {
            var context = PrepareTurn(
                prompt,
                queuedItem.RoutingOverride,
                queuedItem.SpecialSkills,
                queuedItem.AutoRoutingMode,
                queuedItem.Attachments);
            stopRequested = false;
            SetTurnRunning(true);

            await StartCodexTurnAsync(prompt, context);
            queuePaused = false;
            activeFileChangeEntry = null;
            Timeline.Add(new TimelineEntry(UserRole("YOU", queuedItem.SpecialSkills), prompt));
            followUpQueue.Remove(queuedItem.Id);
            SetTurnRunning(true);
            ScrollToLatest();
            return true;
        }
        catch (Exception error)
        {
            currentTurnId = null;
            queuePaused = true;
            SetTurnRunning(false);
            ShowRouteError(FriendlyError(error));
            return false;
        }
    }

    private TurnLaunchContext PrepareTurn(
        string prompt,
        RoutingOverride? oneTurnOverride,
        IReadOnlyList<SpecialSkillId> specialSkills,
        AutoRoutingMode autoRoutingMode,
        IReadOnlyList<AppServerAttachment>? attachments = null)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        var project = services.Workspace.GetProject(projectId);
        var normalizedSkills = services.Backend.Descriptor.Supports(AgentBackendCapabilities.Skills)
            ? SpecialSkillCatalog.Normalize(specialSkills)
            : [];
        var resolvedAttachments = attachments?.ToArray() ?? Attachments.ToArray();
        if (resolvedAttachments.Length > 0 &&
            !services.Backend.Descriptor.Supports(AgentBackendCapabilities.Attachments))
        {
            throw new AgentBackendCapabilityException(
                services.Backend.Descriptor,
                AgentBackendCapabilities.Attachments);
        }
        var remainingQuota = ((App)Application.Current).Monitor.State.Snapshot?.Limits
            .Select(limit => (double?)limit.RemainingPercent)
            .Min();
        var route = services.Router.Decide(new RoutingRequest(
            prompt,
            task.WorkingDirectory,
            services.Models.Select(model => model.ToRoutingOption()).ToArray(),
            project.RoutingLock,
            task.RoutingLock,
            oneTurnOverride,
            normalizedSkills,
            services.Workspace.State.RoutingPolicy,
            remainingQuota));
        var adaptivePlan = services.CreateAdaptiveRoutingPlan(route, autoRoutingMode);

        lastDecision = route;
        runtime.SetRoute(route);
        ShowRouteDecision(route, normalizedSkills, adaptivePlan);
        ShowAdaptivePlan(adaptivePlan);
        return new TurnLaunchContext(
            task,
            route,
            services.ResolveTurnSkills(normalizedSkills, adaptivePlan),
            resolvedAttachments);
    }

    private async Task StartCodexTurnAsync(string prompt, TurnLaunchContext context)
    {
        var threadId = await EnsureThreadAsync(prompt, context);
        try
        {
            var checkpoint = await services.Checkpoints.CaptureBeforeAsync(context.Task.WorkingDirectory);
            await services.Workspace.SetTaskCheckpointAsync(projectId, taskId, checkpoint);
        }
        catch (Exception error) when (error is GitCommandException or DirectoryNotFoundException)
        {
            Activity.Add(new ActivityEntry("CHECKPOINT", $"Tracked-file checkpoint unavailable: {error.Message}"));
        }

        var turn = await services.Backend.StartTurnAsync(
            threadId,
            WithActiveGoal(prompt),
            context.Task.WorkingDirectory,
            context.Route,
            skills: context.AppServerSkills,
            attachments: context.Attachments);
        currentTurnId = turn.Turn.Id;
        await CleanupManagedVoiceNotesAsync(context.Attachments);
        Attachments.Clear();
    }

    private string WithActiveGoal(string prompt)
    {
        var goal = services.GetGoal(taskId);
        return goal is null || goal.State is not GoalRunState.Active
            ? prompt
            : $"{prompt}\n\n<active_goal>\n{goal.ToPrompt()}\n</active_goal>";
    }

    private async Task<string> EnsureThreadAsync(string prompt, TurnLaunchContext context)
    {
        if (!string.IsNullOrWhiteSpace(currentThreadId))
        {
            return currentThreadId;
        }

        var started = await services.Backend.StartThreadAsync(
            context.Task.WorkingDirectory,
            context.Route,
            context.Route.ModelSource is not RoutingSource.Auto,
            configuration: services.AdaptiveThreadConfiguration);
        var threadId = started.Thread.Id;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codex started a thread without returning an id.");
        }

        currentThreadId = threadId;
        await services.Workspace.AttachThreadAsync(projectId, taskId, threadId);
        var title = BuildTaskTitle(prompt);
        await services.Workspace.SetTaskTitleAsync(projectId, taskId, title);
        RenderTask(services.Workspace.GetTask(projectId, taskId));
        services.NotifyWorkspaceChanged();
        if (!services.Backend.Descriptor.Supports(AgentBackendCapabilities.ThreadRenaming))
        {
            return threadId;
        }

        try
        {
            await services.Backend.SetThreadNameAsync(threadId, title);
        }
        catch (Exception error)
        {
            Activity.Add(new ActivityEntry(
                "THREAD NAME",
                $"The task was created, but its Codex title could not be updated: {error.Message}"));
        }

        return threadId;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentThreadId is null || currentTurnId is null)
        {
            return;
        }

        StopButton.IsEnabled = false;
        stopRequested = true;
        runtime.SetState(TaskRunState.Stopping);
        queuePaused = true;
        UpdateQueueUi();
        try
        {
            await services.Backend.InterruptTurnAsync(currentThreadId, currentTurnId);
        }
        catch (Exception error)
        {
            stopRequested = false;
            queuePaused = false;
            ShowRouteError(error.Message);
            UpdateComposerState();
        }
    }

    private void Codex_NotificationReceived(object? sender, AppServerEvent notification)
    {
        if (!MatchesCurrentThread(notification.Parameters))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => _ = HandleNotificationSafelyAsync(notification));
    }

    private async Task HandleNotificationSafelyAsync(AppServerEvent notification)
    {
        try
        {
            await HandleNotificationAsync(notification);
        }
        catch (Exception error)
        {
            Activity.Add(new ActivityEntry("CLIENT", $"Could not process {notification.Method}: {error.Message}"));
            ShowRouteError("Codex sent an update that this app could not process. The task may still be running.");
        }
    }

    private async Task HandleNotificationAsync(AppServerEvent notification)
    {
        var parameters = notification.Parameters;
        switch (notification.Method)
        {
            case "turn/started":
                currentTurnId = ReadString(parameters, "turn", "id") ?? ReadString(parameters, "turnId");
                stopRequested = false;
                queuePaused = false;
                SetTurnRunning(true);
                break;
            case "item/agentMessage/delta":
                AppendAssistantDelta(ReadString(parameters, "delta") ?? string.Empty);
                break;
            case "item/started":
                if (parameters.TryGetProperty("item", out var startedItem))
                {
                    RenderStartedItem(startedItem);
                }
                break;
            case "item/completed":
                if (parameters.TryGetProperty("item", out var completedItem))
                {
                    RenderCompletedItem(completedItem, isHistorical: false);
                }
                break;
            case "turn/plan/updated":
                RenderPlan(parameters);
                break;
            case "turn/diff/updated":
                DiffTextBox.Text = ReadString(parameters, "diff") ?? string.Empty;
                await RefreshEnvironmentAsync();
                break;
            case "item/commandExecution/outputDelta":
                AppendActivityOutput("COMMAND OUTPUT", ReadString(parameters, "delta") ?? string.Empty);
                break;
            case "item/fileChange/outputDelta":
                AppendActivityOutput("PATCH", ReadString(parameters, "delta") ?? string.Empty);
                break;
            case "model/rerouted":
                await HandleModelRerouteAsync(parameters);
                break;
            case "warning":
            case "error":
                Activity.Add(new ActivityEntry(notification.Method.ToUpperInvariant(), ExtractMessage(parameters)));
                break;
            case "turn/completed":
                var status = ReadString(parameters, "turn", "status") ?? "completed";
                var shouldDispatchNext = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) &&
                                         !stopRequested;
                var completedTurnId = currentTurnId;
                currentTurnId = null;
                streamingAssistantEntry = null;
                runtime.SetState(string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                    ? TaskRunState.Completed
                    : TaskRunState.Failed,
                    string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : $"The turn ended with status '{status}'.");
                UpdateComposerState();
                await FinalizeCheckpointAsync(completedTurnId);
                await RefreshEnvironmentAsync();
                WorkReceipt? receipt = null;
                var policy = services.Workspace.GetProject(projectId).VerificationPolicy ?? VerificationPolicy.Disabled;
                try
                {
                    WorkReceiptStatusText.Text = policy.IsEnabled ? "Running verification…" : "Recording work receipt…";
                    WorkReceiptDetailText.Text = policy.IsEnabled ? DescribeCheckCount(policy.Steps.Count) : "Capturing route and Git evidence";
                    receipt = await services.FinalizeWorkReceiptAsync(
                        projectId,
                        taskId,
                        completedTurnId,
                        status);
                    RenderWorkReceipt();
                }
                catch (Exception error)
                {
                    Activity.Add(new ActivityEntry("WORK RECEIPT", $"Could not record completion evidence: {error.Message}"));
                    WorkReceiptStatusText.Text = "Receipt needs attention";
                    WorkReceiptDetailText.Text = FriendlyError(error);
                }

                var verificationBlocked = receipt?.BlocksContinuation(policy) is true ||
                                          (policy.IsEnabled && receipt is null);
                shouldDispatchNext &= !verificationBlocked;
                if (verificationBlocked)
                {
                    queuePaused = true;
                    ShowQueueMessage(
                        "Queued work is paused because the verification gate did not pass. Review the work receipt, then rerun verification.",
                        InfoBarSeverity.Warning);
                }
                if (followUpQueue.Peek() is not null)
                {
                    if (shouldDispatchNext && isPageLoaded)
                    {
                        queuePaused = false;
                        await DispatchNextQueuedAsync();
                    }
                    else if (!shouldDispatchNext)
                    {
                        queuePaused = true;
                        UpdateQueueUi();
                        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            ShowQueueMessage(
                                $"The queue is paused because the turn ended with status '{status}'.",
                                InfoBarSeverity.Warning);
                        }
                    }
                    else
                    {
                        queuePaused = false;
                        UpdateQueueUi();
                    }
                }

                stopRequested = false;
                if (!isPageLoaded)
                {
                    Unsubscribe();
                }
                break;
        }

        ScrollToLatest();
    }

    private async Task FinalizeCheckpointAsync(string? turnId)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        if (task.LastCheckpoint is not { AfterTree: null } checkpoint)
        {
            return;
        }

        try
        {
            var completed = await services.Checkpoints.CompleteAsync(checkpoint, turnId);
            await services.Workspace.SetTaskCheckpointAsync(projectId, taskId, completed);
            services.NotifyWorkspaceChanged();
        }
        catch (Exception error)
        {
            Activity.Add(new ActivityEntry("CHECKPOINT", $"Could not finalize checkpoint: {error.Message}"));
        }
    }

    private void RenderStartedItem(JsonElement item)
    {
        var type = ReadString(item, "type");
        switch (type)
        {
            case "commandExecution":
                Activity.Add(new ActivityEntry("COMMAND", ReadString(item, "command") ?? "Running command"));
                break;
            case "fileChange":
                Activity.Add(new ActivityEntry("FILE CHANGE", DescribeFileChanges(item)));
                break;
            case "mcpToolCall":
                Activity.Add(new ActivityEntry("TOOL", $"{ReadString(item, "server")}/{ReadString(item, "tool")}"));
                break;
            case "dynamicToolCall":
                Activity.Add(new ActivityEntry("TOOL", ReadString(item, "tool") ?? "Tool call"));
                break;
            case "collabAgentToolCall":
                Activity.Add(new ActivityEntry("MODEL HANDOFF", DescribeCollaborationItem(item)));
                break;
            case "subAgentActivity":
                Activity.Add(new ActivityEntry(
                    "AGENT",
                    $"{ReadString(item, "kind") ?? "activity"}: {ReadString(item, "agentPath") ?? "subagent"}"));
                break;
            case "webSearch":
                Activity.Add(new ActivityEntry("WEB SEARCH", ReadString(item, "query") ?? "Searching the web"));
                break;
            case "imageView":
                Activity.Add(new ActivityEntry("IMAGE", ReadString(item, "path") ?? "Viewing image"));
                break;
            case "sleep":
                Activity.Add(new ActivityEntry("WAIT", "Waiting before the next step."));
                break;
            case "reasoning":
                Activity.Add(new ActivityEntry("REASONING", "Working through the request…"));
                break;
        }
    }

    private void RenderCompletedItem(JsonElement item, bool isHistorical)
    {
        var type = ReadString(item, "type");
        switch (type)
        {
            case "userMessage":
                activeFileChangeEntry = null;
                if (isHistorical)
                {
                    var userText = ReadUserMessage(item);
                    if (!string.IsNullOrWhiteSpace(userText))
                    {
                        Timeline.Add(new TimelineEntry("YOU", userText));
                    }
                }
                break;
            case "agentMessage":
                var agentText = ReadString(item, "text");
                if (string.IsNullOrWhiteSpace(agentText))
                {
                    break;
                }

                if (!isHistorical)
                {
                    agentText = ApplyGoalDefinitionFromAgentMessage(agentText);
                }
                if (string.IsNullOrWhiteSpace(agentText))
                {
                    break;
                }

                if (streamingAssistantEntry is not null && !isHistorical)
                {
                    streamingAssistantEntry.Text = agentText;
                    streamingAssistantEntry = null;
                }
                else
                {
                    Timeline.Add(new TimelineEntry("CODEX", agentText));
                }
                break;
            case "plan":
                Activity.Add(new ActivityEntry("PLAN", ReadString(item, "text") ?? string.Empty));
                break;
            case "reasoning":
                Activity.Add(new ActivityEntry("REASONING", JoinStringArray(item, "summary")));
                break;
            case "commandExecution":
                var command = ReadString(item, "command") ?? "Command";
                var output = ReadString(item, "aggregatedOutput");
                var exitCode = item.TryGetProperty("exitCode", out var exitElement) && exitElement.ValueKind == JsonValueKind.Number
                    ? $"exit {exitElement.GetInt32()}"
                    : "completed";
                Activity.Add(new ActivityEntry("COMMAND", string.IsNullOrWhiteSpace(output)
                    ? $"{command}\n{exitCode}"
                    : $"{command}\n{output}"));
                break;
            case "fileChange":
                RenderFileChange(item);
                break;
            case "mcpToolCall":
                Activity.Add(new ActivityEntry("TOOL", $"{ReadString(item, "server")}/{ReadString(item, "tool")} · {ReadString(item, "status")}"));
                break;
            case "dynamicToolCall":
                Activity.Add(new ActivityEntry("TOOL", $"{ReadString(item, "tool")} · {ReadString(item, "status")}"));
                break;
            case "collabAgentToolCall":
                Activity.Add(new ActivityEntry("MODEL HANDOFF", DescribeCollaborationItem(item)));
                break;
            case "subAgentActivity":
                Activity.Add(new ActivityEntry(
                    "AGENT",
                    $"{ReadString(item, "kind") ?? "activity"}: {ReadString(item, "agentPath") ?? "subagent"}"));
                break;
            case "webSearch":
                Activity.Add(new ActivityEntry("WEB SEARCH", ReadString(item, "query") ?? "Web search completed"));
                break;
            case "imageView":
                Activity.Add(new ActivityEntry("IMAGE", ReadString(item, "path") ?? "Image viewed"));
                break;
            case "imageGeneration":
                Activity.Add(new ActivityEntry("IMAGE GENERATION", ReadString(item, "status") ?? "completed"));
                break;
        }
    }

    private void RenderPlan(JsonElement parameters)
    {
        var lines = new List<string>();
        if (parameters.TryGetProperty("explanation", out var explanation) && explanation.ValueKind == JsonValueKind.String)
        {
            lines.Add(explanation.GetString()!);
        }

        if (parameters.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in plan.EnumerateArray())
            {
                var status = ReadString(step, "status") ?? "pending";
                var text = ReadString(step, "step") ?? string.Empty;
                lines.Add($"{status}: {text}");
            }
        }

        var summary = string.Join(Environment.NewLine, lines);
        Activity.Add(new ActivityEntry("PLAN", summary));
        if (!string.IsNullOrWhiteSpace(summary))
        {
            ContextPlanText.Text = summary;
        }
    }

    private void AppendAssistantDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (streamingAssistantEntry is null)
        {
            streamingAssistantEntry = new TimelineEntry("CODEX", string.Empty);
            Timeline.Add(streamingAssistantEntry);
        }

        streamingAssistantEntry.Text += delta;
    }

    private void AppendActivityOutput(string kind, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (Activity.LastOrDefault()?.Kind == kind)
        {
            var current = Activity[^1];
            Activity[^1] = new ActivityEntry(current.Kind, current.Text + delta);
        }
        else
        {
            Activity.Add(new ActivityEntry(kind, delta));
        }
    }

    private void Codex_ApprovalRequested(object? sender, AppServerRequest request)
    {
        if (!MatchesCurrentThread(request.Parameters))
        {
            return;
        }

        if (!isPageLoaded)
        {
            _ = RespondWithSafeDenialAsync(request);
            return;
        }

        DispatcherQueue.TryEnqueue(async () => await HandleApprovalAsync(request));
    }

    private async Task HandleApprovalAsync(AppServerRequest request)
    {
        await approvalGate.WaitAsync();
        try
        {
            Activity.Add(new ActivityEntry("APPROVAL", ApprovalSummary(request)));
            switch (request.Method)
            {
                case "item/commandExecution/requestApproval":
                    await HandleDecisionApprovalAsync(
                        request,
                        "Allow command?",
                        ReadString(request.Parameters, "command") ?? ExtractMessage(request.Parameters));
                    break;
                case "item/fileChange/requestApproval":
                    await HandleDecisionApprovalAsync(
                        request,
                        "Allow file changes?",
                        ReadString(request.Parameters, "reason") ?? "Codex wants to modify files outside its current sandbox allowance.");
                    break;
                case "item/permissions/requestApproval":
                    await HandlePermissionApprovalAsync(request);
                    break;
                case "item/tool/requestUserInput":
                    await HandleUserInputAsync(request);
                    break;
                case "mcpServer/elicitation/request":
                    await HandleElicitationAsync(request);
                    break;
                case "execCommandApproval":
                    await HandleLegacyCommandApprovalAsync(request);
                    break;
                case "applyPatchApproval":
                    await HandleLegacyPatchApprovalAsync(request);
                    break;
                case "item/tool/call":
                    await services.Backend.RespondToApprovalAsync(
                        request,
                        new { contentItems = Array.Empty<object>(), success = false });
                    Activity.Add(new ActivityEntry("TOOL", "Declined an unregistered dynamic tool call."));
                    break;
                default:
                    await RespondWithSafeDenialAsync(request);
                    Activity.Add(new ActivityEntry("APPROVAL", $"Declined unsupported request: {request.Method}"));
                    break;
            }
        }
        catch (Exception error)
        {
            ShowRouteError($"Approval failed: {error.Message}");
        }
        finally
        {
            if (currentThreadId is { } threadId)
            {
                services.MarkApprovalHandled(threadId, request);
            }
            approvalGate.Release();
        }
    }

    private async Task HandleDecisionApprovalAsync(AppServerRequest request, string title, string message)
    {
        var result = await ShowConfirmDialogAsync(title, message, "Allow once", "Deny");
        var decision = result is ContentDialogResult.Primary ? "accept" : "decline";
        await services.Backend.RespondToApprovalAsync(request, new { decision });
        Activity.Add(new ActivityEntry("APPROVAL", result is ContentDialogResult.Primary ? "Allowed once" : "Denied"));
    }

    private async Task HandlePermissionApprovalAsync(AppServerRequest request)
    {
        var reason = ReadString(request.Parameters, "reason") ?? "Codex is requesting additional permissions for this turn.";
        var result = await ShowConfirmDialogAsync("Allow extra permissions?", reason, "Allow this turn", "Deny");
        if (result is ContentDialogResult.Primary && request.Parameters.TryGetProperty("permissions", out var permissions))
        {
            await services.Backend.RespondToApprovalAsync(request, new
            {
                permissions = permissions.Clone(),
                scope = "turn",
            });
        }
        else
        {
            await services.Backend.RespondToApprovalAsync(request, new
            {
                permissions = new { },
                scope = "turn",
            });
        }
    }

    private async Task HandleUserInputAsync(AppServerRequest request)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };
        var inputs = new List<(string Id, FrameworkElement Control)>();
        if (request.Parameters.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in questions.EnumerateArray())
            {
                var id = ReadString(question, "id") ?? Guid.NewGuid().ToString();
                panel.Children.Add(new TextBlock
                {
                    Text = ReadString(question, "question") ?? "Codex needs your input",
                    TextWrapping = TextWrapping.Wrap,
                });

                FrameworkElement control;
                if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                {
                    var combo = new ComboBox { MinWidth = 360, DisplayMemberPath = "Label" };
                    combo.ItemsSource = options.EnumerateArray()
                        .Select(option => new InputOption(
                            ReadString(option, "label") ?? string.Empty,
                            ReadString(option, "description") ?? string.Empty))
                        .ToArray();
                    combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
                    control = combo;
                }
                else if (question.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True)
                {
                    control = new PasswordBox { MinWidth = 360 };
                }
                else
                {
                    control = new TextBox { MinWidth = 360, TextWrapping = TextWrapping.Wrap };
                }

                panel.Children.Add(control);
                inputs.Add((id, control));
            }
        }

        var dialog = CreateDialog("Codex needs input", panel, "Continue", "Cancel");
        var result = await dialog.ShowAsync();
        var answers = new Dictionary<string, object>();
        if (result is ContentDialogResult.Primary)
        {
            foreach (var (id, control) in inputs)
            {
                var value = control switch
                {
                    ComboBox combo when combo.SelectedItem is InputOption option => option.Label,
                    PasswordBox password => password.Password,
                    TextBox text => text.Text,
                    _ => string.Empty,
                };
                answers[id] = new { answers = new[] { value } };
            }
        }

        await services.Backend.RespondToApprovalAsync(request, new { answers });
    }

    private async Task HandleElicitationAsync(AppServerRequest request)
    {
        var message = ReadString(request.Parameters, "message") ?? "An external tool needs input before it can continue.";
        var result = await ShowConfirmDialogAsync("External tool request", message, "Continue", "Deny");
        await services.Backend.RespondToApprovalAsync(request, new
        {
            action = result is ContentDialogResult.Primary ? "accept" : "decline",
            content = result is ContentDialogResult.Primary ? new { } : null,
            _meta = (object?)null,
        });
    }

    private async Task HandleLegacyCommandApprovalAsync(AppServerRequest request)
    {
        var command = request.Parameters.TryGetProperty("command", out var commandElement) &&
                      commandElement.ValueKind == JsonValueKind.Array
            ? string.Join(" ", commandElement.EnumerateArray().Select(element => element.GetString()))
            : "Codex wants to run a command.";
        var result = await ShowConfirmDialogAsync("Allow command?", command, "Allow once", "Deny");
        await services.Backend.RespondToApprovalAsync(
            request,
            new { decision = result is ContentDialogResult.Primary ? "approved" : "denied" });
    }

    private async Task HandleLegacyPatchApprovalAsync(AppServerRequest request)
    {
        var reason = ReadString(request.Parameters, "reason") ?? "Codex wants to apply file changes.";
        var result = await ShowConfirmDialogAsync("Allow file changes?", reason, "Allow once", "Deny");
        await services.Backend.RespondToApprovalAsync(
            request,
            new { decision = result is ContentDialogResult.Primary ? "approved" : "denied" });
    }

    private Task RespondWithSafeDenialAsync(AppServerRequest request)
    {
        object response = request.Method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" =>
                new { decision = "decline" },
            "execCommandApproval" or "applyPatchApproval" =>
                new { decision = "denied" },
            "item/permissions/requestApproval" =>
                new { permissions = new { }, scope = "turn" },
            "item/tool/requestUserInput" => new { answers = new Dictionary<string, object>() },
            "mcpServer/elicitation/request" =>
                new { action = "cancel", content = (object?)null, _meta = (object?)null },
            "item/tool/call" => new { contentItems = Array.Empty<object>(), success = false },
            _ => new { decision = "decline" },
        };
        return services.Backend.RespondToApprovalAsync(request, response);
    }

    private async Task DispatchNextQueuedAsync()
    {
        if (!isPageLoaded || turnIsRunning)
        {
            return;
        }

        await turnActionGate.WaitAsync();
        try
        {
            if (!isPageLoaded || turnIsRunning || followUpQueue.Peek() is not { } item)
            {
                return;
            }

            composerActionBusy = true;
            queuePaused = false;
            UpdateComposerState();
            await StartPromptTurnAsync(item);
        }
        finally
        {
            composerActionBusy = false;
            UpdateComposerState();
            turnActionGate.Release();
        }
    }

    private async void ResumeQueueButton_Click(object sender, RoutedEventArgs e)
    {
        await DispatchNextQueuedAsync();
    }

    private async void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderDecisionContext();
        if (isPopulatingModelMode || ModelCombo.SelectedItem is not ModelChoice { AutoMode: { } mode })
        {
            return;
        }

        try
        {
            await services.Workspace.SetAutoRoutingModeAsync(mode);
        }
        catch (Exception error)
        {
            ShowRouteError($"The Full Auto preference could not be saved: {error.Message}");
        }
    }

    private void DecisionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderDecisionContext();

    private void MoveQueuedUp_Click(object sender, RoutedEventArgs e)
    {
        if (ReadQueuedId(sender) is { } id)
        {
            followUpQueue.MoveUp(id);
        }
    }

    private void MoveQueuedDown_Click(object sender, RoutedEventArgs e)
    {
        if (ReadQueuedId(sender) is { } id)
        {
            followUpQueue.MoveDown(id);
        }
    }

    private async void EditQueued_Click(object sender, RoutedEventArgs e)
    {
        if (ReadQueuedId(sender) is not { } id || followUpQueue.Get(id) is not { } item)
        {
            return;
        }

        var editor = new TextBox
        {
            Text = item.Prompt,
            AcceptsReturn = true,
            MinWidth = 320,
            MinHeight = 120,
            MaxHeight = 280,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = CreateDialog("Edit queued message", editor, "Save", "Cancel");
        if (await dialog.ShowAsync() is ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(editor.Text))
        {
            followUpQueue.Edit(id, editor.Text);
        }
    }

    private async void DeleteQueued_Click(object sender, RoutedEventArgs e)
    {
        if (ReadQueuedId(sender) is { } id)
        {
            var item = followUpQueue.Get(id);
            followUpQueue.Remove(id);
            if (item is not null)
            {
                await CleanupManagedVoiceNotesAsync(item.Attachments ?? []);
            }

            if (followUpQueue.Peek() is null)
            {
                queuePaused = false;
            }
        }
    }

    private async void SteerQueued_Click(object sender, RoutedEventArgs e)
    {
        if (ReadQueuedId(sender) is not { } id)
        {
            return;
        }

        await turnActionGate.WaitAsync();
        try
        {
            if (followUpQueue.Get(id) is not { } item)
            {
                return;
            }

            composerActionBusy = true;
            UpdateComposerState();
            if (turnIsRunning && currentThreadId is not null && currentTurnId is not null)
            {
                if (await SteerPromptAsync(item))
                {
                    followUpQueue.Remove(item.Id);
                    await CleanupManagedVoiceNotesAsync(item.Attachments ?? []);
                }
            }
            else if (!turnIsRunning)
            {
                ShowRouteError("There is no active turn to steer. Choose Run next to start the queued message.");
            }
        }
        finally
        {
            composerActionBusy = false;
            UpdateComposerState();
            turnActionGate.Release();
        }
    }

    private void RefreshQueuedFollowUps()
    {
        var snapshot = followUpQueue.Snapshot();
        QueuedFollowUps.Clear();
        for (var index = 0; index < snapshot.Count; index++)
        {
            var item = snapshot[index];
            QueuedFollowUps.Add(new QueuedFollowUpView(
                item.Id,
                $"{index + 1}.",
                item.Prompt,
                DescribeQueuedRoute(item.RoutingOverride, item.SpecialSkills, item.AutoRoutingMode),
                index > 0,
                index < snapshot.Count - 1));
        }

        if (snapshot.Count == 0)
        {
            queuePaused = false;
        }

        UpdateQueueUi();
    }

    private string DescribeQueuedRoute(
        RoutingOverride? routingOverride,
        IReadOnlyList<SpecialSkillId> specialSkills,
        AutoRoutingMode autoRoutingMode)
    {
        string route;
        if (routingOverride is null || routingOverride.IsEmpty)
        {
            route = "Routes when sent; current task/project locks apply";
        }
        else
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(routingOverride.ModelId))
            {
                parts.Add(DisplayModel(routingOverride.ModelId));
            }

            if (!string.IsNullOrWhiteSpace(routingOverride.Effort))
            {
                parts.Add(DisplayEffort(routingOverride.Effort));
            }

            if (routingOverride.Permission is { } permission and not PermissionLevel.AutoSafe)
            {
                parts.Add(DisplayPermission(permission));
            }

            route = $"One-turn route: {string.Join(" / ", parts)}";
        }

        route = autoRoutingMode is AutoRoutingMode.Adaptive
            ? $"{route} / Auto: adaptive phases"
            : $"{route} / Auto: one model";
        return specialSkills.Count > 0
            ? $"{route} / Skills: {DescribeSkills(specialSkills)}"
            : route;
    }

    private static Guid? ReadQueuedId(object sender)
    {
        if (sender is not FrameworkElement element || element.Tag is null)
        {
            return null;
        }

        if (element.Tag is Guid id)
        {
            return id;
        }

        return Guid.TryParse(element.Tag.ToString(), out var parsed) ? parsed : null;
    }

    private async void LockTask_Click(object sender, RoutedEventArgs e)
    {
        var routingLock = await BuildLockOverrideAsync();
        if (routingLock is null)
        {
            return;
        }

        await services.Workspace.SetTaskLockAsync(projectId, taskId, routingLock);
        RenderLock();
        ShowLockMessage("Task routing locked. One-turn choices can still override it.");
    }

    private async void LockProject_Click(object sender, RoutedEventArgs e)
    {
        var routingLock = await BuildLockOverrideAsync();
        if (routingLock is null)
        {
            return;
        }

        await services.Workspace.SetProjectLockAsync(projectId, routingLock);
        RenderLock();
        ShowLockMessage("Project routing locked. Task locks and one-turn choices take precedence.");
    }

    private async void UnlockTask_Click(object sender, RoutedEventArgs e)
    {
        await services.Workspace.SetTaskLockAsync(projectId, taskId, null);
        RenderLock();
        ShowLockMessage("Task routing unlocked.");
    }

    private async void UnlockProject_Click(object sender, RoutedEventArgs e)
    {
        await services.Workspace.SetProjectLockAsync(projectId, null);
        RenderLock();
        ShowLockMessage("Project routing unlocked.");
    }

    private async Task<RoutingOverride?> BuildSelectedOverrideAsync()
    {
        var permission = SelectedPermission();
        var confirmed = false;
        if (permission is PermissionLevel.FullAccess)
        {
            var result = await ShowConfirmDialogAsync(
                "Confirm full access",
                "This turn can read and write anywhere available to your Windows account and is not sandboxed. Full Auto never selects this level.",
                "Use full access",
                "Cancel");
            if (result is not ContentDialogResult.Primary)
            {
                return null;
            }

            confirmed = true;
        }

        var value = new RoutingOverride(
            (ModelCombo.SelectedItem as ModelChoice)?.ModelId,
            (EffortCombo.SelectedItem as EffortChoice)?.Effort,
            permission,
            confirmed);
        return value.IsEmpty ? null : value;
    }

    private async Task<RoutingOverride?> BuildLockOverrideAsync()
    {
        var selected = await BuildSelectedOverrideAsync();
        if (selected is not null)
        {
            return selected;
        }

        if (SelectedPermission() is PermissionLevel.FullAccess)
        {
            return null;
        }

        if (lastDecision is null)
        {
            ShowRouteError("Choose a model, effort, or permission before creating a lock, or send one Auto-routed turn first.");
            return null;
        }

        var confirmed = false;
        if (lastDecision.Permission is PermissionLevel.FullAccess)
        {
            var result = await ShowConfirmDialogAsync(
                "Confirm full-access lock",
                "This lock will keep using unsandboxed full access until you unlock it. Full Auto never selects this level.",
                "Create full-access lock",
                "Cancel");
            if (result is not ContentDialogResult.Primary)
            {
                return null;
            }

            confirmed = true;
        }

        return new RoutingOverride(lastDecision.ModelId, lastDecision.Effort, lastDecision.Permission, confirmed);
    }

    private PermissionLevel? SelectedPermission()
    {
        return (PermissionCombo.SelectedItem as PermissionChoice)?.Permission;
    }

    private AutoRoutingMode SelectedAutoRoutingMode()
    {
        return (ModelCombo.SelectedItem as ModelChoice)?.AutoMode
               ?? services.Workspace.State.AutoRoutingMode;
    }

    private IReadOnlyList<SpecialSkillId> ActiveSpecialSkills()
    {
        return SpecialSkillCatalog.Normalize(services.Workspace.State.EnabledSpecialSkills);
    }

    private static string DescribeSkills(IEnumerable<SpecialSkillId> skills)
    {
        return string.Join(
            ", ",
            SpecialSkillCatalog.Normalize(skills)
                .Select(skill => SpecialSkillCatalog.Get(skill).DisplayName));
    }

    private static string UserRole(string role, IReadOnlyList<SpecialSkillId> skills)
    {
        return skills.Count > 0 ? $"{role} / {DescribeSkills(skills).ToUpperInvariant()}" : role;
    }

    private static string DescribeRoute(
        RoutingDecision route,
        IReadOnlyList<SpecialSkillId> skills,
        AdaptiveRoutingPlan adaptivePlan)
    {
        var routeDescription = skills.Count > 0
            ? $"{route.Reason} Special skills attached: {DescribeSkills(skills)}."
            : route.Reason;
        return $"{routeDescription} {adaptivePlan.Reason}";
    }

    private async Task<ContentDialogResult> ShowConfirmDialogAsync(
        string title,
        string message,
        string primaryText,
        string closeText)
    {
        return await CreateDialog(
            title,
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            primaryText,
            closeText).ShowAsync();
    }

    private ContentDialog CreateDialog(string title, object content, string primaryText, string closeText)
    {
        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
    }

    private void RenderGoal()
    {
        var goal = services.GetGoal(taskId);
        if (goal is null)
        {
            GoalOutcomeBox.Text = string.Empty;
            GoalConstraintsBox.Text = string.Empty;
            GoalVerificationBox.Text = string.Empty;
            ContextPlanText.Text = "No active goal";
            return;
        }

        GoalOutcomeBox.Text = goal.Outcome;
        GoalConstraintsBox.Text = goal.Constraints;
        GoalVerificationBox.Text = goal.Verification;
        GoalPanel.Visibility = Visibility.Visible;
        ContextPlanText.Text = goal.State is GoalRunState.Paused
            ? $"Paused · {goal.Outcome}"
            : goal.Outcome;
    }

    private async Task<GoalPrompt> PrepareGoalPromptAsync(string prompt)
    {
        var requestedGoal = captureNextPromptAsGoal
            ? prompt
            : ExtractGoalRequest(prompt) ??
              (ActiveSpecialSkills().Contains(SpecialSkillId.PromptMasterCodex) && ContainsGoalIntent(prompt)
                  ? prompt
                  : null);
        if (string.IsNullOrWhiteSpace(requestedGoal))
        {
            return new GoalPrompt(prompt, prompt);
        }

        captureNextPromptAsGoal = false;
        PromptBox.PlaceholderText = DefaultPromptPlaceholder;
        var provisionalGoal = new GoalDefinition(
            requestedGoal,
            "Codex will refine constraints from the request and the workspace before making changes.",
            "Codex will run the relevant verification and report evidence before completion.");
        await services.SaveGoalAsync(projectId, taskId, provisionalGoal);
        RenderGoal();
        return new GoalPrompt(
            requestedGoal,
            $"<goal_setup>\nThe user has started goal mode with this intent:\n{requestedGoal}\n\n" +
            "Before working, emit one compact machine-readable goal definition exactly in this form:\n" +
            "<goal_definition>{\"outcome\":\"...\",\"constraints\":\"...\",\"verification\":\"...\"}</goal_definition>\n" +
            "Then proceed directly with the task. Do not wait for confirmation. Keep the definition specific to the workspace, permissions, and user intent.\n" +
            $"</goal_setup>\n\n{requestedGoal}");
    }

    private static string? ExtractGoalRequest(string prompt)
    {
        var trimmed = prompt.Trim();
        foreach (var prefix in new[] { "/goal ", "goal: ", "goal " })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var request = trimmed[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(request) ? null : request;
            }
        }

        return null;
    }

    private static bool ContainsGoalIntent(string prompt) =>
        Regex.IsMatch(prompt, @"\bgoal\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string ApplyGoalDefinitionFromAgentMessage(string message)
    {
        const string openingTag = "<goal_definition>";
        const string closingTag = "</goal_definition>";
        var start = message.IndexOf(openingTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return message;
        }

        var end = message.IndexOf(closingTag, start + openingTag.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return message;
        }

        var payload = message[(start + openingTag.Length)..end].Trim();
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var outcome = ReadString(root, "outcome");
            if (string.IsNullOrWhiteSpace(outcome))
            {
                return message;
            }

            var goal = new GoalDefinition(
                outcome,
                ReadString(root, "constraints") ?? string.Empty,
                ReadString(root, "verification") ?? string.Empty);
            _ = SaveGoalDefinitionFromCodexAsync(goal);
            return string.Concat(message.AsSpan(0, start), message.AsSpan(end + closingTag.Length)).Trim();
        }
        catch (JsonException)
        {
            return message;
        }
    }

    private async Task SaveGoalDefinitionFromCodexAsync(GoalDefinition goal)
    {
        try
        {
            await services.SaveGoalAsync(projectId, taskId, goal);
            RenderGoal();
        }
        catch (Exception error)
        {
            Activity.Add(new ActivityEntry("GOAL", $"Codex goal update could not be saved: {error.Message}"));
        }
    }

    private void ToggleGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (services.GetGoal(taskId) is null)
        {
            captureNextPromptAsGoal = true;
            PromptBox.PlaceholderText = "Describe the goal, then send it to start Codex";
            PromptBox.Focus(FocusState.Programmatic);
            ShowQueueMessage("Describe the goal and send it. Codex will fill the goal fields and begin the work.");
            return;
        }

        GoalPanel.Visibility = GoalPanel.Visibility is Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (GoalPanel.Visibility is Visibility.Visible)
        {
            GoalOutcomeBox.Focus(FocusState.Programmatic);
        }
    }

    private async void SaveGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GoalOutcomeBox.Text))
        {
            ShowRouteError("A goal needs a concrete outcome.");
            return;
        }

        await services.SaveGoalAsync(
            projectId,
            taskId,
            new GoalDefinition(
                GoalOutcomeBox.Text,
                GoalConstraintsBox.Text,
                GoalVerificationBox.Text,
                GoalRunState.Active));
        RenderGoal();
        ShowQueueMessage("Goal mode is active for this task.");
    }

    private async void PauseGoalButton_Click(object sender, RoutedEventArgs e)
    {
        var goal = services.GetGoal(taskId);
        if (goal is null)
        {
            return;
        }

        await services.SaveGoalAsync(
            projectId,
            taskId,
            goal with
            {
                State = goal.State is GoalRunState.Paused ? GoalRunState.Active : GoalRunState.Paused,
            });
        RenderGoal();
        ShowQueueMessage(services.GetGoal(taskId)?.State is GoalRunState.Paused
            ? "Goal mode is paused."
            : "Goal mode resumed.");
    }

    private async void ClearGoalButton_Click(object sender, RoutedEventArgs e)
    {
        await services.SaveGoalAsync(projectId, taskId, null);
        RenderGoal();
        GoalPanel.Visibility = Visibility.Collapsed;
    }

    private async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        var window = ((App)Application.Current).MainWindow;
        if (window is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var selected = await picker.PickMultipleFilesAsync();
        await AddAttachmentsAsync(selected);
    }

    private async void Composer_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var selected = await e.DataView.GetStorageItemsAsync();
        await AddAttachmentsAsync(selected.OfType<StorageFile>());
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Attach files";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async Task AddAttachmentsAsync(IEnumerable<StorageFile> selected)
    {
        var project = services.Workspace.GetProject(projectId);
        foreach (var file in selected)
        {
            AppServerAttachment attachment;
            try
            {
                attachment = AttachmentPolicy.Validate(file.Path, project.Folders, allowOutsideProject: false);
            }
            catch (InvalidOperationException)
            {
                var confirmation = await ShowConfirmDialogAsync(
                    "Attach file outside project?",
                    $"{file.Path}\n\nCodex will be allowed to read this file for the next message.",
                    "Attach",
                    "Cancel");
                if (confirmation is not ContentDialogResult.Primary)
                {
                    continue;
                }

                attachment = AttachmentPolicy.Validate(file.Path, project.Folders, allowOutsideProject: true);
            }

            if (!Attachments.Any(candidate =>
                    candidate.Path.Equals(attachment.Path, StringComparison.OrdinalIgnoreCase)))
            {
                Attachments.Add(attachment);
            }
        }

        AttachmentPanel.Visibility = Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentList.SelectedItem is AppServerAttachment attachment)
        {
            Attachments.Remove(attachment);
            _ = CleanupManagedVoiceNotesAsync([attachment]);
        }

        AttachmentPanel.Visibility = Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (isRecordingAudio)
        {
            await StopAndAttachVoiceNoteAsync();
            return;
        }

        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        composerActionBusy = true;
        UpdateComposerState();
        try
        {
            audioCapture = new MediaCapture();
            await audioCapture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            });
            var audioFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "VoiceNotes",
                CreationCollisionOption.OpenIfExists);
            activeRecording = await audioFolder.CreateFileAsync(
                $"codex-voice-note-{Guid.NewGuid():N}.wav",
                CreationCollisionOption.ReplaceExisting);
            await audioCapture.StartRecordToStorageFileAsync(
                MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto),
                activeRecording);
            isRecordingAudio = true;
            UpdateComposerState();
        }
        catch (Exception error)
        {
            await DisposeAudioCaptureAsync();
            ShowRouteError($"Could not start microphone recording. {FriendlyError(error)}");
        }
        finally
        {
            composerActionBusy = false;
            UpdateComposerState();
        }
    }

    private async Task StopAndAttachVoiceNoteAsync()
    {
        if (audioCapture is null || activeRecording is null)
        {
            isRecordingAudio = false;
            UpdateComposerState();
            return;
        }

        try
        {
            composerActionBusy = true;
            isRecordingAudio = false;
            UpdateComposerState();
            await audioCapture.StopRecordAsync();
            var recording = activeRecording;
            activeRecording = null;
            audioCapture.Dispose();
            audioCapture = null;
            managedVoiceNotePaths.Add(recording.Path);
            Attachments.Add(new AppServerAttachment(recording.Path, "Voice note.wav"));
            AttachmentPanel.Visibility = Visibility.Visible;
            PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text)
                ? VoiceNoteInstruction
                : $"{PromptBox.Text.TrimEnd()}\n\n{VoiceNoteInstruction}";
            PromptBox.Focus(FocusState.Programmatic);
        }
        catch (Exception error)
        {
            ShowRouteError($"Could not add the voice note. {FriendlyError(error)}");
        }
        finally
        {
            audioCapture?.Dispose();
            audioCapture = null;
            composerActionBusy = false;
            UpdateComposerState();
        }
    }

    private async Task DisposeAudioCaptureAsync()
    {
        audioCapture?.Dispose();
        audioCapture = null;
        if (activeRecording is not null)
        {
            try
            {
                await activeRecording.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
                // Temporary recordings are also cleaned by the operating system.
            }

            activeRecording = null;
        }
    }

    private async Task DiscardAudioRecordingAsync()
    {
        try
        {
            if (audioCapture is not null && isRecordingAudio)
            {
                await audioCapture.StopRecordAsync();
            }
        }
        catch
        {
            // The capture session may already be shutting down with the page.
        }
        finally
        {
            isRecordingAudio = false;
            await DisposeAudioCaptureAsync();
        }
    }

    private async Task CleanupManagedVoiceNotesAsync(IEnumerable<AppServerAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            if (!managedVoiceNotePaths.Remove(attachment.Path))
            {
                continue;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(attachment.Path);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
                // A failed cleanup must not disrupt a completed or queued Codex task.
            }
        }
    }

    private async void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentThreadId is null)
        {
            ShowRouteError("Start the task before compacting its context.");
            return;
        }

        if (turnIsRunning)
        {
            ShowRouteError("Wait for the active turn to finish before compacting context.");
            return;
        }

        runtime.SetContext(runtime.Snapshot.Context with { IsCompacting = true });
        try
        {
            await services.Backend.StartCompactionAsync(currentThreadId);
            ShowQueueMessage("Codex started context compaction.");
        }
        catch (Exception error)
        {
            runtime.SetContext(runtime.Snapshot.Context with { IsCompacting = false });
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void ForkButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentThreadId is null || turnIsRunning)
        {
            ShowRouteError("A conversation can be branched after its active turn finishes.");
            return;
        }

        try
        {
            var task = services.Workspace.GetTask(projectId, taskId);
            var fork = await services.Backend.ForkThreadAsync(
                currentThreadId,
                task.WorkingDirectory,
                configuration: services.AdaptiveThreadConfiguration);
            var branchedTask = await services.Workspace.CreateTaskAsync(
                projectId,
                $"Branch of {task.Title}",
                task.WorkingDirectory,
                task.ExecutionLocation,
                task.Worktree);
            await services.Workspace.AttachThreadAsync(projectId, branchedTask.Id, fork.Thread.Id);
            services.NotifyWorkspaceChanged();
            ((App)Application.Current).ShowTask(projectId, branchedTask.Id);
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void ContinueFreshButton_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        var project = services.Workspace.GetProject(projectId);
        var goal = services.GetGoal(taskId);
        IReadOnlyList<string> changes = [];
        try
        {
            changes = (await services.Worktrees.InspectAsync(task.WorkingDirectory)).Changes;
        }
        catch
        {
            // Non-Git projects still get a metadata-only handoff.
        }

        var draft = new StringBuilder()
            .AppendLine("Continue this task from a fresh Codex context.")
            .AppendLine()
            .AppendLine($"Task: {task.Title}")
            .AppendLine($"Working directory: {task.WorkingDirectory}")
            .AppendLine(goal is null ? "Goal: not explicitly defined" : $"Goal: {goal.Outcome}")
            .AppendLine(goal is null || string.IsNullOrWhiteSpace(goal.Constraints) ? string.Empty : $"Constraints: {goal.Constraints}")
            .AppendLine(goal is null || string.IsNullOrWhiteSpace(goal.Verification) ? string.Empty : $"Verification: {goal.Verification}")
            .AppendLine(changes.Count == 0 ? "Git state: clean or unavailable" : $"Git changes:\n{string.Join(Environment.NewLine, changes)}")
            .AppendLine()
            .AppendLine("First inspect the repository and current changes, then state the remaining plan before editing.")
            .ToString();
        var editor = new TextBox
        {
            Text = draft,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 620,
            MinHeight = 340,
        };
        var dialog = CreateDialog("Fresh continuation handoff", editor, "Create task", "Cancel");
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary || string.IsNullOrWhiteSpace(editor.Text))
        {
            return;
        }

        var newTask = await services.Workspace.CreateTaskAsync(
            projectId,
            $"Continue {task.Title}",
            project.Folders[0]);
        services.SetDraftPrompt(newTask.Id, editor.Text);
        services.NotifyWorkspaceChanged();
        ((App)Application.Current).ShowTask(projectId, newTask.Id);
    }

    private async void GitReviewButton_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        try
        {
            var dialog = new GitReviewDialog(services.Worktrees, task.WorkingDirectory)
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
            await RefreshEnvironmentAsync();
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void CompareBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        try
        {
            var dialog = new GitReviewDialog(
                services.Worktrees,
                task.WorkingDirectory,
                compareBranches: true)
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
            await RefreshEnvironmentAsync();
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(task.WorkingDirectory);
        Process.Start(startInfo);
    }

    private void CopyBranch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(gitEnvironment?.BranchName))
        {
            ShowRouteError("This checkout is detached and has no branch name to copy.");
            return;
        }

        var content = new DataPackage();
        content.SetText(gitEnvironment.BranchName);
        Clipboard.SetContent(content);
        ShowEnvironmentMessage($"Copied branch '{gitEnvironment.BranchName}'.");
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var branchName = await PromptForBranchAsync();
            if (branchName is null)
            {
                return;
            }

            ShowEnvironmentMessage($"Created and switched to '{branchName}'.");
            await RefreshEnvironmentAsync();
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async Task<string?> PromptForBranchAsync()
    {
        var editor = new TextBox
        {
            Header = "Branch name",
            PlaceholderText = "codex/my-change",
            MinWidth = 360,
        };
        var dialog = CreateDialog("Create branch", editor, "Create", "Cancel");
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(editor.Text))
        {
            return null;
        }

        var task = services.Workspace.GetTask(projectId, taskId);
        return await services.Worktrees.CreateBranchAsync(task.WorkingDirectory, editor.Text);
    }

    private async void CommitPushButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEnvironmentAsync(showErrors: true);
        var environment = gitEnvironment;
        if (environment is null)
        {
            return;
        }

        CommitPushButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(environment.BranchName))
            {
                var branchName = await PromptForBranchAsync();
                if (branchName is null)
                {
                    return;
                }

                await RefreshEnvironmentAsync();
                environment = gitEnvironment ?? environment;
            }

            var task = services.Workspace.GetTask(projectId, taskId);
            if (!environment.IsClean)
            {
                var message = new TextBox
                {
                    Header = "Commit message",
                    PlaceholderText = "Describe this change",
                    MinWidth = 420,
                };
                var stageAll = new CheckBox
                {
                    Content = "Stage all changed and untracked files",
                    IsChecked = true,
                };
                var pushAfterCommit = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(environment.UpstreamBranch)
                        ? "Publish branch after committing"
                        : "Push after committing",
                    IsChecked = false,
                };
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(message);
                panel.Children.Add(stageAll);
                panel.Children.Add(pushAfterCommit);
                var dialog = CreateDialog("Commit changes", panel, "Commit", "Cancel");
                if (await dialog.ShowAsync() is not ContentDialogResult.Primary ||
                    string.IsNullOrWhiteSpace(message.Text))
                {
                    return;
                }

                if (!await EnsureVerificationGatePassedAsync(environment))
                {
                    return;
                }

                await services.Worktrees.CommitAsync(
                    task.WorkingDirectory,
                    message.Text,
                    stageAll.IsChecked is true);
                if (pushAfterCommit.IsChecked is true)
                {
                    await services.Worktrees.PushAsync(task.WorkingDirectory);
                    ShowEnvironmentMessage("Committed and pushed the branch.");
                }
                else
                {
                    ShowEnvironmentMessage("Committed the selected changes.");
                }

                await RefreshEnvironmentAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(environment.UpstreamBranch) && environment.AheadBy == 0)
            {
                ShowEnvironmentMessage("The working tree is clean and the branch has nothing to push.");
                return;
            }

            var publish = string.IsNullOrWhiteSpace(environment.UpstreamBranch);
            var result = await ShowConfirmDialogAsync(
                publish ? "Publish this branch?" : "Push committed changes?",
                publish
                    ? $"Publish '{environment.BranchName}' to origin and set its upstream?"
                    : $"Push {environment.AheadBy} commit{(environment.AheadBy == 1 ? string.Empty : "s")} to '{environment.UpstreamBranch}'?",
                publish ? "Publish" : "Push",
                "Cancel");
            if (result is not ContentDialogResult.Primary)
            {
                return;
            }

            if (!await EnsureVerificationGatePassedAsync(environment))
            {
                return;
            }

            await services.Worktrees.PushAsync(task.WorkingDirectory);
            ShowEnvironmentMessage(publish ? "Published the branch to origin." : "Pushed the branch.");
            await RefreshEnvironmentAsync();
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
        finally
        {
            CommitPushButton.IsEnabled = gitEnvironment is not null;
        }
    }

    private async void UndoCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (turnIsRunning)
        {
            ShowRouteError("Wait for the active turn to finish before undoing its tracked changes.");
            return;
        }

        var checkpoint = services.Workspace.GetTask(projectId, taskId).LastCheckpoint;
        if (checkpoint?.AfterTree is null || checkpoint.IsUndone)
        {
            ShowRouteError("There is no completed turn checkpoint available to undo.");
            return;
        }

        try
        {
            var preview = await services.Checkpoints.PreviewAsync(checkpoint);
            if (await ShowConfirmDialogAsync(
                    "Undo last turn's tracked changes?",
                    $"{preview}\n\nUntracked files are left untouched.",
                    "Undo",
                    "Cancel") is not ContentDialogResult.Primary)
            {
                return;
            }

            var undone = await services.Checkpoints.UndoAsync(checkpoint);
            await services.Workspace.SetTaskCheckpointAsync(projectId, taskId, undone);
            if (activeFileChangeEntry is not null)
            {
                foreach (var file in activeFileChangeEntry.FileChanges)
                {
                    file.IsUndone = true;
                }
            }
            services.NotifyWorkspaceChanged();
            ShowQueueMessage("The last turn's tracked changes were undone. Untracked files were preserved.");
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void UndoFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileChangeLine fileChange } || fileChange.IsUndone)
        {
            return;
        }

        if (turnIsRunning)
        {
            ShowRouteError("Wait for the active turn to finish before undoing a file.");
            return;
        }

        var checkpoint = services.Workspace.GetTask(projectId, taskId).LastCheckpoint;
        if (checkpoint?.AfterTree is null || checkpoint.IsUndone)
        {
            ShowRouteError("There is no completed turn checkpoint available for this file.");
            return;
        }

        try
        {
            if (await ShowConfirmDialogAsync(
                    "Undo changes to this file?",
                    $"{fileChange.Path}\n\nOnly this tracked file will be restored. Other files in the turn stay unchanged.",
                    "Undo file",
                    "Cancel") is not ContentDialogResult.Primary)
            {
                return;
            }

            var updated = await services.Checkpoints.UndoFileAsync(checkpoint, fileChange.Path);
            await services.Workspace.SetTaskCheckpointAsync(projectId, taskId, updated);
            fileChange.IsUndone = true;
            services.NotifyWorkspaceChanged();
            ShowQueueMessage($"Undid the turn's tracked changes to {System.IO.Path.GetFileName(fileChange.Path)}.");
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void RedoCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (turnIsRunning)
        {
            ShowRouteError("Wait for the active turn to finish before redoing tracked changes.");
            return;
        }

        var checkpoint = services.Workspace.GetTask(projectId, taskId).LastCheckpoint;
        if (checkpoint?.AfterTree is null || !checkpoint.IsUndone)
        {
            ShowRouteError("There is no undone turn checkpoint available to redo.");
            return;
        }

        try
        {
            var preview = await services.Checkpoints.PreviewAsync(checkpoint);
            if (await ShowConfirmDialogAsync(
                    "Redo last turn's tracked changes?",
                    $"{preview}\n\nUntracked files are left untouched.",
                    "Redo",
                    "Cancel") is not ContentDialogResult.Primary)
            {
                return;
            }

            var redone = await services.Checkpoints.RedoAsync(checkpoint);
            await services.Workspace.SetTaskCheckpointAsync(projectId, taskId, redone);
            if (activeFileChangeEntry is not null)
            {
                foreach (var file in activeFileChangeEntry.FileChanges)
                {
                    file.IsUndone = false;
                }
            }
            services.NotifyWorkspaceChanged();
            ShowQueueMessage("The last turn's tracked changes were restored.");
        }
        catch (Exception error)
        {
            ShowRouteError(FriendlyError(error));
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentThreadId is null)
        {
            ShowRouteError("Start the task before exporting its conversation.");
            return;
        }

        var window = ((App)Application.Current).MainWindow;
        if (window is null)
        {
            return;
        }

        var privacy = await ShowConfirmDialogAsync(
            "Export conversation?",
            "The exported file contains prompt and response text. Choose its destination carefully.",
            "Continue",
            "Cancel");
        if (privacy is not ContentDialogResult.Primary)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = services.Workspace.GetTask(projectId, taskId).Title };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var thread = (await services.Backend.ReadThreadAsync(currentThreadId)).Thread;
        var content = file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ConversationExportService.ExportJson(thread)
            : ConversationExportService.ExportMarkdown(thread);
        await File.WriteAllTextAsync(file.Path, content);
        ShowQueueMessage($"Conversation exported to {file.Path}.");
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        var profile = services.Workspace.GetProject(projectId).ExecutionProfile ?? ProjectExecutionProfile.Native;
        Process.Start(services.ExecutionProfiles.CreateTerminalStartInfo(profile, task.WorkingDirectory));
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        var task = services.Workspace.GetTask(projectId, taskId);
        var profile = services.Workspace.GetProject(projectId).ExecutionProfile ?? ProjectExecutionProfile.Native;
        Process.Start(services.ExecutionProfiles.CreateEditorStartInfo(profile, task.WorkingDirectory));
    }

    private void SetTurnRunning(bool running)
    {
        runtime.SetState(running ? TaskRunState.Running : TaskRunState.Idle);
        UpdateComposerState();
        if (!running && !isPageLoaded)
        {
            Unsubscribe();
        }
    }

    private void UpdateComposerState()
    {
        if (SendButton is null)
        {
            return;
        }

        SendButton.Content = "Send";
        AutomationProperties.SetName(
            SendButton,
            turnIsRunning
                ? "Queue a message for the next Codex turn"
                : "Send message");
        SendButton.IsEnabled = !composerActionBusy;
        TranscribeButton.Content = isRecordingAudio ? "Stop" : "Mic";
        AutomationProperties.SetName(
            TranscribeButton,
            isRecordingAudio ? "Stop recording and attach the voice note" : "Record a voice note for Codex");
        var supportsAttachments = services.Backend.Descriptor.Supports(AgentBackendCapabilities.Attachments);
        AddAttachmentButton.IsEnabled = supportsAttachments && !composerActionBusy;
        TranscribeButton.IsEnabled = supportsAttachments && (!composerActionBusy || isRecordingAudio);
        StopButton.IsEnabled =
            services.Backend.Descriptor.Supports(AgentBackendCapabilities.TurnInterruption) &&
            turnIsRunning && currentTurnId is not null && !stopRequested;
        ModelCombo.IsEnabled = !composerActionBusy;
        EffortCombo.IsEnabled = !composerActionBusy;
        PermissionCombo.IsEnabled = !composerActionBusy;
        LockButton.IsEnabled = !composerActionBusy;
        UpdateQueueUi();
    }

    private void UpdateQueueUi()
    {
        if (QueuePanel is null)
        {
            return;
        }

        var count = QueuedFollowUps.Count;
        QueuePanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResumeQueueButton.Visibility = count > 0 && !turnIsRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        QueueStatusText.Text = count == 0
            ? string.Empty
            : queuePaused
                ? $"{count} queued · waiting"
                : turnIsRunning
                    ? $"{count} queued · runs after this turn"
                    : $"{count} queued · starting next";
    }

    private void ResetOneTurnSelectors()
    {
        SelectPersistedAutoRoutingMode();
        EffortCombo.SelectedIndex = 0;
        PermissionCombo.SelectedIndex = 0;
    }

    private void SelectPersistedAutoRoutingMode()
    {
        ModelCombo.SelectedIndex = services.Workspace.State.AutoRoutingMode is AutoRoutingMode.SingleModel
            ? 1
            : 0;
    }

    private void ShowLockMessage(string message)
    {
        RouteInfoBar.Severity = InfoBarSeverity.Success;
        RouteInfoBar.Title = "Routing preference saved";
        RouteInfoBar.Message = message;
        RouteInfoBar.IsOpen = true;
    }

    private void ShowQueueMessage(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        RouteInfoBar.Severity = severity;
        RouteInfoBar.Title = severity is InfoBarSeverity.Warning ? "Follow-up queued safely" : "Follow-up queued";
        RouteInfoBar.Message = message;
        RouteInfoBar.IsOpen = true;
    }

    private void ShowEnvironmentMessage(string message)
    {
        RouteInfoBar.Severity = InfoBarSeverity.Success;
        RouteInfoBar.Title = "Environment updated";
        RouteInfoBar.Message = message;
        RouteInfoBar.IsOpen = true;
    }

    private void ShowRouteDecision(
        RoutingDecision route,
        IReadOnlyList<SpecialSkillId> specialSkills,
        AdaptiveRoutingPlan adaptivePlan)
    {
        RouteInfoBar.Severity = InfoBarSeverity.Informational;
        RouteInfoBar.Title = $"{DisplayModel(route.ModelId)} / {DisplayEffort(route.Effort)} / {DisplayPermission(route.Permission)}";
        RouteInfoBar.Message = DescribeRoute(route, specialSkills, adaptivePlan);
        RouteInfoBar.IsOpen = true;
        ContextRouteText.Text = adaptivePlan.IsEnabled
            ? $"{DisplayModel(route.ModelId)} coordinates adaptive workers"
            : $"{DisplayModel(route.ModelId)} · {DisplayEffort(route.Effort)}";
        ContextSkillsText.Text = specialSkills.Count == 0
            ? $"{DisplayPermission(route.Permission)} · Special skills off"
            : $"{DisplayPermission(route.Permission)} · {DescribeSkills(specialSkills)}";
    }

    private void ShowAdaptivePlan(AdaptiveRoutingPlan adaptivePlan)
    {
        if (!adaptivePlan.IsEnabled)
        {
            return;
        }

        var workers = string.Join(
            "; ",
            adaptivePlan.AgentProfiles.Select(profile =>
                $"{profile.DisplayName}: {DisplayModel(profile.ModelId)} / {DisplayEffort(profile.Effort)}"));
        Activity.Add(new ActivityEntry(
            "ADAPTIVE ROUTE",
            $"{DisplayModel(adaptivePlan.CoordinatorModelId)} coordinates. {workers}."));
    }

    private void ShowRouteError(string message)
    {
        RouteInfoBar.Severity = InfoBarSeverity.Error;
        RouteInfoBar.Title = "Codex could not continue";
        RouteInfoBar.Message = message;
        RouteInfoBar.IsOpen = true;
    }

    private void ScrollToLatest()
    {
        if (Timeline.Count > 0)
        {
            TimelineList.ScrollIntoView(Timeline[^1]);
        }
    }

    private bool MatchesCurrentThread(JsonElement parameters)
    {
        var threadId = ReadString(parameters, "threadId") ?? ReadString(parameters, "conversationId");
        return string.IsNullOrWhiteSpace(threadId) ||
               (!string.IsNullOrWhiteSpace(currentThreadId) &&
                string.Equals(threadId, currentThreadId, StringComparison.Ordinal));
    }

    private async Task HandleModelRerouteAsync(JsonElement parameters)
    {
        var from = ReadString(parameters, "fromModel") ?? lastDecision?.ModelId ?? "the locked model";
        var to = ReadString(parameters, "toModel") ?? "another model";
        if (lastDecision?.ModelSource is not null and not RoutingSource.Auto)
        {
            Activity.Add(new ActivityEntry("LOCK VIOLATION", $"Codex tried to reroute {from} to {to}. The turn was stopped."));
            ShowRouteError($"The locked model became unavailable. Codex tried to switch from {from} to {to}, so the turn was stopped instead of silently changing models.");
            stopRequested = true;
            queuePaused = true;
            UpdateQueueUi();
            if (currentThreadId is not null && currentTurnId is not null)
            {
                try
                {
                    await services.Backend.InterruptTurnAsync(currentThreadId, currentTurnId);
                }
                catch (Exception error)
                {
                    Activity.Add(new ActivityEntry("STOP", error.Message));
                }
            }

            return;
        }

        Activity.Add(new ActivityEntry("MODEL", $"Codex rerouted {from} to {to}."));
    }

    private string DescribeCollaborationItem(JsonElement item)
    {
        var tool = ReadString(item, "tool") ?? "agent action";
        var status = ReadString(item, "status") ?? "in progress";
        var model = ReadString(item, "model");
        var effort = ReadString(item, "reasoningEffort");
        if (string.Equals(tool, "spawnAgent", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model))
        {
            var route = string.IsNullOrWhiteSpace(effort)
                ? DisplayModel(model)
                : $"{DisplayModel(model)} / {DisplayEffort(effort)}";
            return $"Coordinator → {route} · {status}";
        }

        return $"{tool} · {status}";
    }

    private string DisplayModel(string id)
    {
        return services.Models.FirstOrDefault(model =>
                   model.Model.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName
               ?? id;
    }

    private static string DisplayEffort(string effort)
    {
        return effort switch
        {
            "xhigh" => "Extra high",
            _ => char.ToUpperInvariant(effort[0]) + effort[1..],
        };
    }

    private static string DisplayPermission(PermissionLevel permission)
    {
        return permission switch
        {
            PermissionLevel.ReadOnly => "Read-only",
            PermissionLevel.WorkspaceWrite => "Workspace-write",
            PermissionLevel.FullAccess => "Full access",
            _ => "Auto safe",
        };
    }

    private static string FriendlyError(Exception error)
    {
        return error switch
        {
            LockedModelUnavailableException => $"{error.Message} Unlock it or choose an available model; it was not silently changed.",
            LockedEffortUnavailableException => $"{error.Message} Unlock it or choose a supported effort; it was not silently changed.",
            FullAccessConfirmationRequiredException => "Full access is locked but was not explicitly confirmed. Recreate that lock and confirm full access.",
            _ => error.Message,
        };
    }

    private static string BuildTaskTitle(string prompt)
    {
        var firstLine = prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                        ?? "New task";
        return firstLine.Length <= 72 ? firstLine : $"{firstLine[..69]}…";
    }

    private static string ApprovalSummary(AppServerRequest request)
    {
        return request.Method switch
        {
            "item/commandExecution/requestApproval" => ReadString(request.Parameters, "command") ?? "Command approval requested",
            "item/fileChange/requestApproval" => ReadString(request.Parameters, "reason") ?? "File change approval requested",
            "item/permissions/requestApproval" => ReadString(request.Parameters, "reason") ?? "Additional permissions requested",
            "item/tool/requestUserInput" => "Codex requested user input",
            "mcpServer/elicitation/request" => ReadString(request.Parameters, "message") ?? "External tool requested input",
            _ => request.Method,
        };
    }

    private static string ReadUserMessage(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var input in content.EnumerateArray())
        {
            var text = ReadString(input, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private void RenderFileChange(JsonElement item)
    {
        var changes = ExtractFileChanges(item);
        if (changes.Count == 0)
        {
            return;
        }

        Activity.Add(new ActivityEntry(
            "FILE CHANGE",
            string.Join(Environment.NewLine, changes.Select(change => change.Path))));
        if (activeFileChangeEntry is null)
        {
            activeFileChangeEntry = new TimelineEntry(
                "CHANGES",
                changes.Count == 1 ? "Edited 1 file" : $"Edited {changes.Count} files",
                changes);
            Timeline.Add(activeFileChangeEntry);
        }
        else
        {
            activeFileChangeEntry.MergeFileChanges(changes);
        }
    }

    private static IReadOnlyList<FileChangeLine> ExtractFileChanges(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return [new FileChangeLine(ReadString(item, "status") ?? "File changes", 0, 0)];
        }

        return changes.EnumerateArray().Select(change =>
        {
            var path = ReadString(change, "path") ?? "file";
            var diff = ReadString(change, "diff") ?? string.Empty;
            var additions = ReadInt(change, "additions") ?? CountDiffLines(diff, '+');
            var deletions = ReadInt(change, "deletions") ?? CountDiffLines(diff, '-');
            return new FileChangeLine(path, additions, deletions);
        }).ToArray();
    }

    private static string DescribeFileChanges(JsonElement item) =>
        string.Join(Environment.NewLine, ExtractFileChanges(item).Select(change => change.Path));

    private static int CountDiffLines(string diff, char prefix) =>
        diff.Split('\n').Count(line =>
            line.Length > 0 &&
            line[0] == prefix &&
            !line.StartsWith(prefix == '+' ? "+++" : "---", StringComparison.Ordinal));

    private static int? ReadInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string JoinStringArray(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, value.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()));
    }

    private static string ExtractMessage(JsonElement parameters)
    {
        return ReadString(parameters, "message")
               ?? ReadString(parameters, "error", "message")
               ?? parameters.ToString();
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadElementString(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.String ? element.GetString() : null;
    }

    private static string? ReadString(JsonElement element, string objectProperty, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(objectProperty, out var nested)
            ? ReadString(nested, property)
            : null;
    }

    private sealed record InputOption(string Label, string Description);
}
