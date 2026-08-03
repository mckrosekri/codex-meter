using CodexDecision.Core.Projects;
using CodexDecision.Core.Conversations;
using CodexDecision.Core.Routing;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CodexMeterTray.Views;

public partial class ShellPage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;
    private Guid? selectedProjectId;
    private bool isInitialized;
    private string searchQuery = string.Empty;
    private Guid? pendingProjectId;
    private Guid? pendingTaskId;
    private Guid? activeProjectId;
    private Guid? activeTaskId;

    public ShellPage()
    {
        InitializeComponent();
        Loaded += ShellPage_Loaded;
        Unloaded += ShellPage_Unloaded;
        ShowWelcome();
    }

    private async void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (isInitialized)
        {
            return;
        }

        isInitialized = true;
        services.Changed += Services_Changed;
        try
        {
            await services.InitializeAsync();

            if (services.Workspace.State.Projects.Count > 0)
            {
                if (pendingProjectId is { } requestedProject && pendingTaskId is { } requestedTask)
                {
                    selectedProjectId = requestedProject;
                    pendingProjectId = null;
                    pendingTaskId = null;
                    RebuildProjects();
                    OpenChat(requestedProject, requestedTask);
                    RenderConnectionState();
                    return;
                }

                var activity = BuildActivityLookups();
                var initialProject = WorkspaceRecencyOrdering.OrderProjects(
                    services.Workspace.State.Projects,
                    activity.Threads,
                    activity.LiveTasks)[0];
                selectedProjectId = initialProject.Id;
                RebuildProjects();
                ShowWelcome();
            }
            else
            {
                RebuildProjects();
                if (services.ThreadIndex.Count > 0)
                {
                    ShowAllChats();
                }
            }
        }
        catch (Exception error)
        {
            ConnectionInfoBar.Message = error.Message;
            ConnectionInfoBar.IsOpen = true;
        }

        RenderConnectionState();
    }

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
        services.Changed -= Services_Changed;
    }

    private void Services_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is ChatPage && activeTaskId is { } taskId)
            {
                services.TaskAttention.MarkReviewed(taskId);
            }

            RenderConnectionState();
            RebuildProjects();
        });
    }

    private void RenderConnectionState()
    {
        ConnectionText.Text = services.ConnectionState switch
        {
            CodexConnectionState.Ready => $"Local Codex · {services.Models.Count} models",
            CodexConnectionState.Connecting => "Connecting to local Codex",
            CodexConnectionState.Recovering => "Recovering local Codex connection",
            CodexConnectionState.Failed => "Local Codex connection failed",
            _ => "Local Codex not started",
        };
        if (services.ConnectionState is CodexConnectionState.Ready)
        {
            ConnectionText.Text = $"Local Codex · {services.ThreadIndex.Count:N0} chats";
        }

        ConnectionDot.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            services.ConnectionState is CodexConnectionState.Ready
                ? "AppGoodBrush"
                : services.ConnectionState is CodexConnectionState.Failed
                    ? "AppDangerBrush"
                    : "AppWarningBrush"];
    }

    private void TaskSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason is not AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        searchQuery = sender.Text.Trim();
        RebuildProjects();
    }

    private async void ShellNavigation_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string command)
        {
            switch (command)
            {
                case "add-project":
                    await AddProjectAsync();
                    return;
                case "new-task":
                    await CreateQuestionTaskAsync();
                    return;
                case "all-chats":
                    ShowAllChats();
                    return;
                case "settings":
                    ContentFrame.Content = new SettingsPage();
                    return;
                case "usage":
                    ContentFrame.Content = new MainPage();
                    return;
                case "scheduled":
                    ContentFrame.Content = new ScheduledPage();
                    return;
                case "integrations":
                    ContentFrame.Content = new IntegrationsPage();
                    return;
            }
        }

        if (args.InvokedItemContainer?.Tag is NavigationTarget target)
        {
            selectedProjectId = target.ProjectId;
            if (target.TaskId is { } taskId)
            {
                OpenChat(target.ProjectId, taskId);
            }

            // Rebuilding MenuItems here can release the native container mid-click.

            return;
        }

    }

    private async Task AddProjectAsync()
    {
        try
        {
            var window = ((App)Application.Current).MainWindow;
            if (window is null)
            {
                return;
            }

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var name = Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar));
            var project = await services.Workspace.AddProjectAsync(name, folder.Path);
            selectedProjectId = project.Id;
            if (services.ConnectionState is CodexConnectionState.Ready)
            {
                await services.SyncProjectThreadsAsync(project.Id);
            }

            RebuildProjects();
            await CreateProjectTaskAsync(project.Id);
        }
        catch (Exception error)
        {
            ConnectionInfoBar.Title = "Project could not be added";
            ConnectionInfoBar.Message = error.Message;
            ConnectionInfoBar.IsOpen = true;
        }
    }

    private async Task CreateQuestionTaskAsync()
    {
        try
        {
            var folder = GetQuestionWorkspaceDirectory();
            var (project, task) = await services.Workspace.CreateQuestionTaskAsync(folder);
            selectedProjectId = project.Id;
            RebuildProjects();
            OpenChat(project.Id, task.Id);
        }
        catch (Exception error)
        {
            ShowShellError("Chat could not be created", error.Message);
        }
    }

    private async Task CreateProjectTaskAsync(Guid projectId)
    {
        var project = services.Workspace.GetProject(projectId);
        var location = new ComboBox
        {
            Header = "Execution location",
            ItemsSource = new[] { "Local checkout", "Managed worktree" },
            SelectedIndex = 0,
            MinWidth = 300,
        };
        var baseReference = new TextBox
        {
            Header = "Worktree base reference",
            Text = "HEAD",
            PlaceholderText = "HEAD, main, or another branch",
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(location);
        content.Children.Add(baseReference);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "New task",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            TaskRecord task;
            if (location.SelectedIndex == 1)
            {
                var metadata = await services.Worktrees.CreateManagedAsync(
                    project.Folders[0],
                    Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(baseReference.Text) ? "HEAD" : baseReference.Text.Trim());
                task = await services.Workspace.CreateTaskAsync(
                    projectId,
                    "New task",
                    metadata.WorktreePath,
                    ExecutionLocation.ManagedWorktree,
                    metadata);
            }
            else
            {
                task = await services.Workspace.CreateTaskAsync(projectId, "New task");
            }

            RebuildProjects();
            OpenChat(projectId, task.Id);
        }
        catch (Exception error)
        {
            ShowShellError("Task could not be created", error.Message);
        }
    }

    private void RebuildProjects()
    {
        while (ShellNavigation.MenuItems.Count > 3)
        {
            ShellNavigation.MenuItems.RemoveAt(3);
        }

        var projects = services.Workspace.State.Projects;
        ShellNavigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Projects" });
        var activity = BuildActivityLookups();
        foreach (var project in WorkspaceRecencyOrdering.OrderProjects(
                     projects,
                     activity.Threads,
                     activity.LiveTasks))
        {
            var projectMatches = string.IsNullOrWhiteSpace(searchQuery) ||
                                 project.Name.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase);
            var orderedTasks = WorkspaceRecencyOrdering.OrderTasks(
                project.Tasks.Where(task =>
                    !task.IsArchived &&
                    (projectMatches ||
                     task.Title.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase))),
                activity.Threads,
                activity.LiveTasks);
            if (!projectMatches && orderedTasks.Count == 0)
            {
                continue;
            }

            // Keep child containers ready even while a project is collapsed. This lets
            // NavigationView expand in place instead of replacing its active item.
            var visibleTaskLimit = !string.IsNullOrWhiteSpace(searchQuery) ? 8 : 12;
            var visibleTasks = orderedTasks.Take(visibleTaskLimit).ToArray();

            var projectItem = new NavigationViewItem
            {
                Content = project.Name,
                Icon = new SymbolIcon(Symbol.Folder),
                Tag = new NavigationTarget(project.Id, null),
            };
            AutomationProperties.SetName(projectItem, $"Project {project.Name}");
            projectItem.ContextFlyout = CreateProjectFlyout(project);

            foreach (var task in visibleTasks)
            {
                projectItem.MenuItems.Add(CreateTaskNavigationItem(project, task));
            }

            if (orderedTasks.Count > visibleTasks.Length && visibleTaskLimit > 0)
            {
                var more = new NavigationViewItem
                {
                    Content = $"Show all {orderedTasks.Count:N0} chats",
                    Icon = new SymbolIcon(Symbol.More),
                    Tag = "all-chats",
                };
                AutomationProperties.SetName(more, $"Show all chats in {project.Name}");
                projectItem.MenuItems.Add(more);
            }

            ShellNavigation.MenuItems.Add(projectItem);
            if (project.Id == selectedProjectId || !string.IsNullOrWhiteSpace(searchQuery))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (projectItem.XamlRoot is not null)
                    {
                        projectItem.IsExpanded = true;
                    }
                });
            }
        }

        if (services.Workspace.State.Projects.Count == 0 && services.ThreadIndex.Count == 0)
        {
            ShowWelcome();
        }
    }

    private void ShowWelcome()
    {
        var welcome = new WelcomePage();
        welcome.NewQuestionRequested += async (_, _) => await CreateQuestionTaskAsync();
        welcome.AddProjectRequested += async (_, _) => await AddProjectAsync();
        ContentFrame.Content = welcome;
    }

    private void ShowAllChats()
    {
        var page = new AllChatsPage();
        page.TaskOpenRequested += (_, request) => NavigateToTask(request.ProjectId, request.TaskId);
        ContentFrame.Content = page;
    }

    public void NavigateToTask(Guid projectId, Guid taskId)
    {
        if (!isInitialized)
        {
            pendingProjectId = projectId;
            pendingTaskId = taskId;
            return;
        }

        OpenChat(projectId, taskId);
    }

    private void OpenChat(Guid projectId, Guid taskId)
    {
        selectedProjectId = projectId;
        var clearedAttention = services.TaskAttention.MarkReviewed(taskId);
        if (activeProjectId == projectId && activeTaskId == taskId && ContentFrame.Content is ChatPage)
        {
            if (clearedAttention)
            {
                RebuildProjects();
            }

            return;
        }

        activeProjectId = projectId;
        activeTaskId = taskId;
        ContentFrame.Content = new ChatPage(projectId, taskId);
        if (clearedAttention)
        {
            RebuildProjects();
        }
    }

    private static Grid CreateTaskLabel(
        TaskRecord task,
        TaskRuntimeSnapshot runtime,
        TaskAttentionKind attention)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = task.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        FrameworkElement? status = attention switch
        {
            TaskAttentionKind.Ready => CreateAttentionDot(
                "Task finished and is ready for review",
                "AppPrimaryBrush"),
            TaskAttentionKind.Failed => CreateAttentionDot(
                "Task stopped and needs review",
                "AppDangerBrush"),
            _ => CreateRuntimeStatus(runtime),
        };
        if (status is not null)
        {
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);
        }

        return grid;
    }

    private static Border CreateAttentionDot(string accessibleName, string brushKey)
    {
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brushKey],
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(dot, accessibleName);
        ToolTipService.SetToolTip(dot, accessibleName);
        return dot;
    }

    private static TextBlock? CreateRuntimeStatus(TaskRuntimeSnapshot runtime)
    {
        var text = runtime.State switch
        {
            TaskRunState.Starting => "Starting",
            TaskRunState.Running when runtime.QueueCount > 0 => $"Running · {runtime.QueueCount}",
            TaskRunState.Running => "Running",
            TaskRunState.WaitingForApproval => "Approval",
            TaskRunState.WaitingForInput => "Input",
            TaskRunState.Paused when runtime.QueueCount > 0 => $"Paused · {runtime.QueueCount}",
            TaskRunState.Paused => "Paused",
            TaskRunState.Stopping => "Stopping",
            TaskRunState.Recovering => "Recovering",
            _ when runtime.QueueCount > 0 => $"Queued · {runtime.QueueCount}",
            _ => null,
        };
        return text is null
            ? null
            : new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppFaintBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
    }

    private NavigationViewItem CreateTaskNavigationItem(ProjectRecord project, TaskRecord task)
    {
        var runtime = services.GetTaskRuntime(project.Id, task.Id).Snapshot;
        var attention = services.TaskAttention.GetAttention(task.Id);
        var item = new NavigationViewItem
        {
            Content = CreateTaskLabel(task, runtime, attention),
            Tag = new NavigationTarget(project.Id, task.Id),
        };
        AutomationProperties.SetName(item, attention switch
        {
            TaskAttentionKind.Ready => $"{task.Title}, finished and ready for review",
            TaskAttentionKind.Failed => $"{task.Title}, stopped and needs review",
            _ => task.Title,
        });
        ToolTipService.SetToolTip(item, task.Title);
        item.ContextFlyout = CreateTaskFlyout(project, task);
        return item;
    }

    private MenuFlyout CreateProjectFlyout(ProjectRecord project)
    {
        var flyout = new MenuFlyout();
        var newTask = new MenuFlyoutItem
        {
            Text = IsQuestionProject(project) ? "New question" : "New task in this project",
        };
        newTask.Click += async (_, _) =>
        {
            if (IsQuestionProject(project))
            {
                await CreateQuestionTaskAsync();
            }
            else
            {
                await CreateProjectTaskAsync(project.Id);
            }
        };
        var rename = new MenuFlyoutItem { Text = "Rename project" };
        rename.Click += async (_, _) => await RenameProjectAsync(project);
        var pin = new MenuFlyoutItem { Text = project.IsPinned ? "Unpin project" : "Pin project" };
        pin.Click += async (_, _) =>
        {
            await services.Workspace.SetProjectPinnedAsync(project.Id, !project.IsPinned);
            services.NotifyWorkspaceChanged();
        };
        var profile = new MenuFlyoutItem { Text = "Execution profile" };
        profile.Click += async (_, _) => await EditExecutionProfileAsync(project);
        flyout.Items.Add(newTask);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(rename);
        flyout.Items.Add(pin);
        flyout.Items.Add(profile);
        return flyout;
    }

    private MenuFlyout CreateTaskFlyout(ProjectRecord project, TaskRecord task)
    {
        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename task" };
        rename.Click += async (_, _) => await RenameTaskAsync(project, task);
        var pin = new MenuFlyoutItem { Text = task.IsPinned ? "Unpin task" : "Pin task" };
        pin.Click += async (_, _) =>
        {
            await services.Workspace.SetTaskPinnedAsync(project.Id, task.Id, !task.IsPinned);
            services.NotifyWorkspaceChanged();
        };
        var archive = new MenuFlyoutItem { Text = "Archive task" };
        archive.Click += async (_, _) =>
        {
            var runtime = services.GetTaskRuntime(project.Id, task.Id).Snapshot;
            if (runtime.State is TaskRunState.Running or TaskRunState.WaitingForApproval or TaskRunState.WaitingForInput)
            {
                ShowShellError("Task is still running", "Stop or finish the active turn before archiving this task.");
                return;
            }

            if (task.Worktree is { } worktree)
            {
                var status = await services.Worktrees.InspectAsync(worktree.WorktreePath);
                var cleanup = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = status.IsClean ? "Archive and remove worktree?" : "Discard worktree changes and archive?",
                    Content = new TextBlock
                    {
                        Text = status.IsClean
                            ? "The managed worktree will be removed. Committed branch history remains in Git."
                            : $"This permanently discards the following uncommitted worktree changes:\n\n{string.Join(Environment.NewLine, status.Changes)}",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 560,
                    },
                    PrimaryButtonText = status.IsClean ? "Remove and archive" : "Discard and archive",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await cleanup.ShowAsync() is not ContentDialogResult.Primary)
                {
                    return;
                }

                await services.Worktrees.RemoveAsync(worktree, discardChanges: !status.IsClean);
            }

            await services.Workspace.SetTaskArchivedAsync(project.Id, task.Id, true);
            services.NotifyWorkspaceChanged();
            ShowWelcome();
        };
        flyout.Items.Add(rename);
        flyout.Items.Add(pin);
        flyout.Items.Add(archive);
        return flyout;
    }

    private async Task RenameProjectAsync(ProjectRecord project)
    {
        var editor = new TextBox { Text = project.Name, MinWidth = 360 };
        var dialog = CreateTextDialog("Rename project", editor);
        if (await dialog.ShowAsync() is ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(editor.Text))
        {
            await services.Workspace.RenameProjectAsync(project.Id, editor.Text);
            services.NotifyWorkspaceChanged();
        }
    }

    private async Task RenameTaskAsync(ProjectRecord project, TaskRecord task)
    {
        var editor = new TextBox { Text = task.Title, MinWidth = 360 };
        var dialog = CreateTextDialog("Rename task", editor);
        if (await dialog.ShowAsync() is ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(editor.Text))
        {
            await services.Workspace.SetTaskTitleAsync(project.Id, task.Id, editor.Text);
            if (!string.IsNullOrWhiteSpace(task.ThreadId))
            {
                await services.Backend.SetThreadNameAsync(task.ThreadId, editor.Text.Trim());
            }

            services.NotifyWorkspaceChanged();
        }
    }

    private async Task EditExecutionProfileAsync(ProjectRecord project)
    {
        var current = project.ExecutionProfile ?? ProjectExecutionProfile.Native;
        var kind = new ComboBox
        {
            Header = "Runtime",
            ItemsSource = new[] { "Native Windows", "WSL" },
            SelectedIndex = current.Kind is ExecutionProfileKind.Wsl ? 1 : 0,
            MinWidth = 320,
        };
        var distro = new TextBox { Header = "WSL distribution", Text = current.WslDistribution ?? string.Empty };
        var shell = new TextBox { Header = "Shell", Text = current.Shell ?? string.Empty, PlaceholderText = "powershell.exe" };
        var editor = new TextBox { Header = "Editor", Text = current.Editor ?? string.Empty, PlaceholderText = "code" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(kind);
        panel.Children.Add(distro);
        panel.Children.Add(shell);
        panel.Children.Add(editor);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Project execution profile",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var profile = new ProjectExecutionProfile(
                kind.SelectedIndex == 1 ? ExecutionProfileKind.Wsl : ExecutionProfileKind.NativeWindows,
                distro.Text.Trim(),
                shell.Text.Trim(),
                editor.Text.Trim());
            await services.ExecutionProfiles.ValidateAsync(profile, project.Folders[0]);
            await services.Workspace.SetProjectExecutionProfileAsync(project.Id, profile);
            services.NotifyWorkspaceChanged();
        }
        catch (Exception error)
        {
            ShowShellError("Execution profile could not be saved", error.Message);
        }
    }

    private ContentDialog CreateTextDialog(string title, TextBox editor) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
        Content = editor,
        PrimaryButtonText = "Save",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    private void ShowShellError(string title, string message)
    {
        ConnectionInfoBar.Title = title;
        ConnectionInfoBar.Message = message;
        ConnectionInfoBar.IsOpen = true;
    }

    private void ShellNavigation_PaneClosing(
        NavigationView sender,
        NavigationViewPaneClosingEventArgs args)
    {
        TaskSearchBox.IsSuggestionListOpen = false;
        PaneHeaderContent.Visibility = Visibility.Collapsed;
    }

    private void ShellNavigation_PaneOpened(NavigationView sender, object args)
    {
        PaneHeaderContent.Visibility = Visibility.Visible;
    }

    private static string GetQuestionWorkspaceDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "CodexDecision", "Questions");
    }

    private static bool IsQuestionProject(ProjectRecord project) =>
        project.Folders.Any(folder => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetQuestionWorkspaceDirectory())),
            StringComparison.OrdinalIgnoreCase));

    private sealed record NavigationTarget(Guid ProjectId, Guid? TaskId);

    private ActivityLookups BuildActivityLookups()
    {
        var threads = services.ThreadIndex
            .GroupBy(entry => entry.ThreadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(entry => entry.UpdatedAt),
                StringComparer.Ordinal);
        var liveTasks = services.TaskRuntimes.SnapshotAll()
            .Where(snapshot => snapshot.State is not TaskRunState.Idle)
            .GroupBy(snapshot => snapshot.TaskId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(snapshot => snapshot.LastActivityAt));
        return new ActivityLookups(threads, liveTasks);
    }

    private sealed record ActivityLookups(
        IReadOnlyDictionary<string, DateTimeOffset> Threads,
        IReadOnlyDictionary<Guid, DateTimeOffset> LiveTasks);
}
