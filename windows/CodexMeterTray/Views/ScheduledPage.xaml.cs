using System.Collections.ObjectModel;
using CodexDecision.Core.Conversations;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMeterTray.Views;

public sealed class ScheduledTaskView
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PurposeLabel { get; set; } = string.Empty;
    public string TargetLabel { get; set; } = string.Empty;
    public string PromptPreview { get; set; } = string.Empty;
    public string ScheduleLabel { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string ToggleLabel { get; set; } = string.Empty;
}

public sealed class AutomationRunView
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string TimeLabel { get; set; } = string.Empty;
    public Visibility UnreadVisibility { get; set; }
}

public sealed record ScheduledTaskTarget(string Label, Guid ProjectId, Guid TaskId);

public sealed record ScheduleChoice(string Label, ScheduleKind Kind);

public partial class ScheduledPage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;

    public ScheduledPage()
    {
        InitializeComponent();
        Loaded += ScheduledPage_Loaded;
        Unloaded += ScheduledPage_Unloaded;
    }

    public ObservableCollection<ScheduledTaskView> ScheduledItems { get; } = [];

    public ObservableCollection<AutomationRunView> RunItems { get; } = [];

    private void ScheduledPage_Loaded(object sender, RoutedEventArgs e)
    {
        services.Changed += Services_Changed;
        Render();
    }

    private void ScheduledPage_Unloaded(object sender, RoutedEventArgs e)
    {
        services.Changed -= Services_Changed;
    }

    private void Services_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        ScheduledItems.Clear();
        foreach (var definition in services.Automations
                     .OrderBy(candidate => !candidate.IsEnabled)
                     .ThenBy(candidate => candidate.NextRunAt))
        {
            var target = ResolveTargetLabel(definition.ProjectId, definition.TaskId);
            var activeRun = services.AutomationRuns.FirstOrDefault(run =>
                run.AutomationId == definition.Id && run.Status is AutomationRunStatus.Running);
            ScheduledItems.Add(new ScheduledTaskView
            {
                Id = definition.Id,
                Name = definition.Name,
                PurposeLabel = definition.Purpose is ScheduledTaskPurpose.Monitor ? "MONITOR" : "FOLLOW-UP",
                TargetLabel = target,
                PromptPreview = Preview(definition.Prompt),
                ScheduleLabel = DescribeSchedule(definition),
                StatusLabel = activeRun is not null
                    ? "Running now"
                    : !definition.IsEnabled
                        ? "Paused"
                        : definition.LastError is not null
                            ? $"Retrying after error · {Preview(definition.LastError, 90)}"
                            : definition.LastRunAt is { } lastRun
                                ? $"Last ran {RelativeTime(lastRun)}"
                                : "Not run yet",
                ToggleLabel = definition.IsEnabled ? "Pause" : "Resume",
            });
        }

        RunItems.Clear();
        foreach (var run in services.AutomationRuns.OrderByDescending(candidate => candidate.StartedAt))
        {
            RunItems.Add(new AutomationRunView
            {
                Id = run.Id,
                Name = run.AutomationName,
                StatusLabel = run.Status switch
                {
                    AutomationRunStatus.Running => "Running",
                    AutomationRunStatus.Failed => "Failed · needs attention",
                    _ when run.NeedsAttention => "Needs attention",
                    _ when run.Purpose is ScheduledTaskPurpose.Monitor => "No change",
                    _ => "Completed",
                },
                Summary = run.Summary,
                TimeLabel = $"{run.Purpose} · {run.StartedAt.ToLocalTime():g}",
                UnreadVisibility = run.NeedsAttention && !run.IsRead
                    ? Visibility.Visible
                    : Visibility.Collapsed,
            });
        }

        var unread = services.AutomationRuns.Count(run => run.NeedsAttention && !run.IsRead);
        var active = services.Automations.Count(definition => definition.IsEnabled);
        HeaderSummaryText.Text = unread > 0
            ? $"{active} active · {unread} run{(unread == 1 ? string.Empty : "s")} need attention"
            : $"{active} active · recurring monitors and task follow-ups run while Open Layer is open";
        RunsTab.Header = unread > 0 ? $"Runs ({unread})" : "Runs";
        SchedulesEmptyState.Visibility = ScheduledItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunsEmptyState.Visibility = RunItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddMonitor_Click(object sender, RoutedEventArgs e) =>
        await ShowAutomationEditorAsync(null, ScheduledTaskPurpose.Monitor);

    private async void AddFollowUp_Click(object sender, RoutedEventArgs e) =>
        await ShowAutomationEditorAsync(null, ScheduledTaskPurpose.FollowUp);

    private async void EditAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationFromSender(sender) is { } definition)
        {
            await ShowAutomationEditorAsync(definition, definition.Purpose);
        }
    }

    private async Task ShowAutomationEditorAsync(
        ScheduledTaskDefinition? existing,
        ScheduledTaskPurpose initialPurpose)
    {
        var targets = services.Workspace.State.Projects.SelectMany(project => project.Tasks
                .Where(task => !task.IsArchived)
                .Select(task => new ScheduledTaskTarget($"{project.Name} / {task.Title}", project.Id, task.Id)))
            .ToArray();
        if (targets.Length == 0)
        {
            ShowError("Create a project task before scheduling work.");
            return;
        }

        var target = new ComboBox
        {
            Header = "Task context",
            ItemsSource = targets,
            DisplayMemberPath = nameof(ScheduledTaskTarget.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        target.SelectedItem = existing is null
            ? targets[0]
            : targets.FirstOrDefault(candidate => candidate.ProjectId == existing.ProjectId &&
                                                  candidate.TaskId == existing.TaskId) ?? targets[0];

        var purpose = new ComboBox
        {
            Header = "Type",
            ItemsSource = new[] { "Monitor", "Scheduled follow-up" },
            SelectedIndex = initialPurpose is ScheduledTaskPurpose.Monitor ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var name = new TextBox
        {
            Header = "Name",
            PlaceholderText = initialPurpose is ScheduledTaskPurpose.Monitor
                ? "Production error monitor"
                : "Deployment follow-up",
            Text = existing?.Name ?? string.Empty,
        };
        var prompt = new TextBox
        {
            Header = "Instructions for every run",
            PlaceholderText = initialPurpose is ScheduledTaskPurpose.Monitor
                ? "Check the latest production errors. Report only new failures or regressions that need action."
                : "Check whether the deployment completed, summarize the result, and continue the task if needed.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 130,
            Text = existing?.Prompt ?? string.Empty,
        };
        var schedule = new ComboBox
        {
            Header = "Schedule",
            DisplayMemberPath = nameof(ScheduleChoice.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var interval = new NumberBox
        {
            Header = "Interval minutes",
            Value = existing?.IntervalMinutes > 0 ? existing.IntervalMinutes : 60,
            Minimum = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var localNext = (existing?.NextRunAt ?? DateTimeOffset.Now.AddMinutes(5)).ToLocalTime();
        var onceDate = new DatePicker { Header = "Run date", Date = localNext };
        var onceTime = new TimePicker { Header = "Run time", Time = localNext.TimeOfDay };
        var dailyAt = new TimePicker
        {
            Header = "Daily local time",
            Time = existing?.DailyAt?.ToTimeSpan() ?? new TimeSpan(9, 0, 0),
        };
        var securityNote = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["AppMutedBrush"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "Runs reuse this task's context, Full Auto routing, and enabled skills. Unattended access is capped at workspace write.",
        };

        void PopulateSchedules()
        {
            var selectedKind = schedule.SelectedItem is ScheduleChoice selected
                ? selected.Kind
                : existing?.Kind ?? (purpose.SelectedIndex == 0 ? ScheduleKind.Interval : ScheduleKind.Once);
            var choices = purpose.SelectedIndex == 0
                ? new[]
                {
                    new ScheduleChoice("Every N minutes", ScheduleKind.Interval),
                    new ScheduleChoice("Daily", ScheduleKind.Daily),
                }
                : new[]
                {
                    new ScheduleChoice("Once", ScheduleKind.Once),
                    new ScheduleChoice("Every N minutes", ScheduleKind.Interval),
                    new ScheduleChoice("Daily", ScheduleKind.Daily),
                };
            schedule.ItemsSource = choices;
            schedule.SelectedItem = choices.FirstOrDefault(choice => choice.Kind == selectedKind) ?? choices[0];
        }

        void UpdateScheduleFields()
        {
            var kind = (schedule.SelectedItem as ScheduleChoice)?.Kind ?? ScheduleKind.Interval;
            interval.Visibility = kind is ScheduleKind.Interval ? Visibility.Visible : Visibility.Collapsed;
            onceDate.Visibility = kind is ScheduleKind.Once ? Visibility.Visible : Visibility.Collapsed;
            onceTime.Visibility = kind is ScheduleKind.Once ? Visibility.Visible : Visibility.Collapsed;
            dailyAt.Visibility = kind is ScheduleKind.Daily ? Visibility.Visible : Visibility.Collapsed;
        }

        PopulateSchedules();
        UpdateScheduleFields();
        purpose.SelectionChanged += (_, _) =>
        {
            PopulateSchedules();
            UpdateScheduleFields();
        };
        schedule.SelectionChanged += (_, _) => UpdateScheduleFields();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(target);
        panel.Children.Add(purpose);
        panel.Children.Add(name);
        panel.Children.Add(prompt);
        panel.Children.Add(schedule);
        panel.Children.Add(interval);
        panel.Children.Add(onceDate);
        panel.Children.Add(onceTime);
        panel.Children.Add(dailyAt);
        panel.Children.Add(securityNote);
        var content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null
                ? initialPurpose is ScheduledTaskPurpose.Monitor ? "Create monitor" : "Schedule follow-up"
                : "Edit scheduled work",
            Content = content,
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        if (target.SelectedItem is not ScheduledTaskTarget selectedTarget ||
            schedule.SelectedItem is not ScheduleChoice selectedSchedule ||
            string.IsNullOrWhiteSpace(name.Text) ||
            string.IsNullOrWhiteSpace(prompt.Text))
        {
            ShowError("Choose a task and provide both a name and durable run instructions.");
            return;
        }

        var selectedPurpose = purpose.SelectedIndex == 0
            ? ScheduledTaskPurpose.Monitor
            : ScheduledTaskPurpose.FollowUp;
        var intervalMinutes = double.IsNaN(interval.Value) ? 60 : Math.Max(1, (int)Math.Round(interval.Value));
        var nextRun = selectedSchedule.Kind switch
        {
            ScheduleKind.Once => ToUtc(onceDate.Date, onceTime.Time),
            ScheduleKind.Daily => NextDaily(DateTimeOffset.UtcNow, TimeOnly.FromTimeSpan(dailyAt.Time)),
            _ => DateTimeOffset.UtcNow.AddMinutes(intervalMinutes),
        };
        if (nextRun <= DateTimeOffset.UtcNow)
        {
            nextRun = DateTimeOffset.UtcNow.AddMinutes(1);
        }

        try
        {
            if (existing is null)
            {
                await services.AddAutomationAsync(
                    selectedTarget.ProjectId,
                    selectedTarget.TaskId,
                    name.Text,
                    prompt.Text,
                    selectedSchedule.Kind,
                    intervalMinutes,
                    selectedSchedule.Kind is ScheduleKind.Daily ? TimeOnly.FromTimeSpan(dailyAt.Time) : null,
                    nextRun,
                    selectedPurpose);
            }
            else
            {
                await services.UpdateAutomationAsync(
                    existing.Id,
                    selectedTarget.ProjectId,
                    selectedTarget.TaskId,
                    name.Text,
                    prompt.Text,
                    selectedSchedule.Kind,
                    intervalMinutes,
                    selectedSchedule.Kind is ScheduleKind.Daily ? TimeOnly.FromTimeSpan(dailyAt.Time) : null,
                    nextRun,
                    selectedPurpose);
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void OpenTask_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationFromSender(sender) is { } definition)
        {
            ((App)Application.Current).ShowTask(definition.ProjectId, definition.TaskId);
        }
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationFromSender(sender) is not { } definition)
        {
            return;
        }

        try
        {
            await services.RunAutomationNowAsync(definition.Id);
            ShowStatus($"{definition.Name} started in its task.", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private async void ToggleAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationFromSender(sender) is { } definition)
        {
            await services.SetAutomationEnabledAsync(definition.Id, !definition.IsEnabled);
        }
    }

    private async void DeleteAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationFromSender(sender) is not { } definition)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {definition.Name}?",
            Content = "The encrypted instructions and this schedule's run history will be removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            await services.RemoveAutomationAsync(definition.Id);
        }
    }

    private async void OpenRun_Click(object sender, RoutedEventArgs e)
    {
        if (RunFromSender(sender) is not { } run)
        {
            return;
        }

        if (run.NeedsAttention && !run.IsRead)
        {
            await services.MarkAutomationRunReadAsync(run.Id);
        }
        ((App)Application.Current).ShowTask(run.ProjectId, run.TaskId);
    }

    private async void MarkRunRead_Click(object sender, RoutedEventArgs e)
    {
        if (RunFromSender(sender) is { } run)
        {
            await services.MarkAutomationRunReadAsync(run.Id);
        }
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e) =>
        await services.MarkAllAutomationRunsReadAsync();

    private ScheduledTaskDefinition? AutomationFromSender(object sender) =>
        sender is FrameworkElement { Tag: Guid id }
            ? services.Automations.FirstOrDefault(candidate => candidate.Id == id)
            : null;

    private AutomationRunRecord? RunFromSender(object sender) =>
        sender is FrameworkElement { Tag: Guid id }
            ? services.AutomationRuns.FirstOrDefault(candidate => candidate.Id == id)
            : null;

    private string ResolveTargetLabel(Guid projectId, Guid taskId)
    {
        var project = services.Workspace.State.Projects.FirstOrDefault(candidate => candidate.Id == projectId);
        var task = project?.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        return project is not null && task is not null
            ? $"{project.Name} / {task.Title}"
            : "Task is no longer available";
    }

    private static string DescribeSchedule(ScheduledTaskDefinition definition)
    {
        var schedule = definition.Kind switch
        {
            ScheduleKind.Once => "Once",
            ScheduleKind.Interval => $"Every {definition.IntervalMinutes} minute{(definition.IntervalMinutes == 1 ? string.Empty : "s")}",
            ScheduleKind.Daily => $"Daily at {definition.DailyAt:HH\\:mm}",
            _ => definition.Kind.ToString(),
        };
        return definition.IsEnabled
            ? $"{schedule} · next {definition.NextRunAt.ToLocalTime():g}"
            : schedule;
    }

    private static string RelativeTime(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value.ToUniversalTime();
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
        return value.ToLocalTime().ToString("g");
    }

    private static string Preview(string value, int maxLength = 180)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength].TrimEnd()}…";
    }

    private static DateTimeOffset ToUtc(DateTimeOffset date, TimeSpan time)
    {
        var local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTimeOffset NextDaily(DateTimeOffset now, TimeOnly time)
    {
        var local = now.ToLocalTime();
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, time.Hour, time.Minute, 0, local.Offset);
        return (candidate <= local ? candidate.AddDays(1) : candidate).ToUniversalTime();
    }

    private void ShowError(string message) => ShowStatus(message, InfoBarSeverity.Error);

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = severity is InfoBarSeverity.Error ? "Scheduled work unavailable" : "Scheduled";
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }
}
