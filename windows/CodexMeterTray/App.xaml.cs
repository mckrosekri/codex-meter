using CodexMeterTray.Services;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CodexMeterTray;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CodexMeterTray.SingleInstance";
    private const string ShowWindowEventName = @"Local\CodexMeterTray.ShowWindow";
    private const string ExitApplicationEventName = @"Local\CodexMeterTray.ExitApplication";

    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Mutex instanceMutex;
    private readonly EventWaitHandle showWindowSignal;
    private readonly EventWaitHandle exitApplicationSignal;
    private readonly CancellationTokenSource signalCancellation = new();
    private readonly bool isPrimaryInstance;
    private DispatcherQueue? dispatcherQueue;
    private Window? window;
    private TaskNotificationService? notifications;
    private bool isExiting;
    private bool isWindowVisible;

    public App()
    {
        UnhandledException += (_, args) =>
            CrashDiagnostics.Record("WinUI unhandled exception", args.Exception, args.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashDiagnostics.Record(
                args.IsTerminating ? "AppDomain terminating exception" : "AppDomain unhandled exception",
                args.ExceptionObject as Exception,
                args.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashDiagnostics.Record("Unobserved task exception", args.Exception);

        instanceMutex = new Mutex(true, InstanceMutexName, out isPrimaryInstance);
        showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        exitApplicationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitApplicationEventName);
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        Monitor = new UsageMonitor();
        Services = new AppServices();
    }

    public UsageMonitor Monitor { get; }

    public AppServices Services { get; }

    public Window? MainWindow => window;

    public TaskbarIcon? TrayIcon { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var exitRequested = HasArgument("--exit");
        var taskTarget = ReadTaskArguments(Environment.GetCommandLineArgs());
        if (!isPrimaryInstance)
        {
            if (exitRequested)
            {
                exitApplicationSignal.Set();
            }
            else
            {
                if (taskTarget is { } target)
                {
                    WritePendingActivation(target.ProjectId, target.TaskId);
                }
                showWindowSignal.Set();
            }

            // Do not keep a secondary WinUI process alive while the primary instance
            // handles the signal. Waiting on the mutex here can strand a headless XAML
            // process during shutdown and has produced COM/XAML failure reports.
            Environment.Exit(0);
            return;
        }

        if (exitRequested)
        {
            instanceMutex.ReleaseMutex();
            Environment.Exit(0);
            return;
        }

        dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(WaitForShowWindowSignal);
        try
        {
            InitializeTrayIcon();
        }
        catch (Exception error)
        {
            CrashDiagnostics.Record("Tray initialization failed", error);
        }
        notifications = new TaskNotificationService(
            () => Services.Workspace.State.NotificationPreference,
            () => isWindowVisible,
            (projectId, taskId) => dispatcherQueue?.TryEnqueue(() => ShowTask(projectId, taskId)),
            message => dispatcherQueue?.TryEnqueue(() =>
            {
                if (TrayIcon is not null)
                {
                    TrayIcon.ToolTipText = message;
                }
            }));
        notifications.Initialize();
        Services.TaskRuntimes.SnapshotChanged += TaskRuntimes_SnapshotChanged;
        Monitor.Changed += Monitor_Changed;
        refreshTimer.Tick += RefreshTimer_Tick;
        refreshTimer.Start();
        try
        {
            await Monitor.RefreshAsync();
        }
        catch (Exception error)
        {
            CrashDiagnostics.Record("Initial usage refresh failed", error);
        }

        if (taskTarget is { } initialTarget)
        {
            ShowTask(initialTarget.ProjectId, initialTarget.TaskId);
        }
        else if (HasArgument("--show"))
        {
            ShowWindow();
        }
    }

    private void WaitForShowWindowSignal()
    {
        var handles = new WaitHandle[]
        {
            showWindowSignal,
            exitApplicationSignal,
            signalCancellation.Token.WaitHandle,
        };

        while (!signalCancellation.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled == 0)
            {
                dispatcherQueue?.TryEnqueue(HandleShowSignal);
                continue;
            }

            if (signaled == 1)
            {
                dispatcherQueue?.TryEnqueue(ExitApplication);
            }
            break;
        }
    }

    private void HandleShowSignal()
    {
        if (ReadPendingActivation() is { } target)
        {
            ShowTask(target.ProjectId, target.TaskId);
            return;
        }

        ShowWindow();
    }

    private static bool HasArgument(string expected)
    {
        return Environment.GetCommandLineArgs().Any(argument =>
            argument.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static (Guid ProjectId, Guid TaskId)? ReadTaskArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 2 < arguments.Count; index++)
        {
            if (arguments[index].Equals("--show-task", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(arguments[index + 1], out var projectId) &&
                Guid.TryParse(arguments[index + 2], out var taskId))
            {
                return (projectId, taskId);
            }
        }

        return null;
    }

    private static void WritePendingActivation(Guid projectId, Guid taskId)
    {
        var path = PendingActivationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{projectId:D}|{taskId:D}");
    }

    private static (Guid ProjectId, Guid TaskId)? ReadPendingActivation()
    {
        var path = PendingActivationPath();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var parts = File.ReadAllText(path).Split('|', 2);
            File.Delete(path);
            return parts.Length == 2 && Guid.TryParse(parts[0], out var projectId) && Guid.TryParse(parts[1], out var taskId)
                ? (projectId, taskId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string PendingActivationPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "CodexDecision", "pending-task-activation.txt");
    }

    private void InitializeTrayIcon()
    {
        var showCommand = (XamlUICommand)Resources["ShowWindowCommand"];
        showCommand.ExecuteRequested += (_, _) => ShowWindow();

        var refreshCommand = (XamlUICommand)Resources["RefreshCommand"];
        refreshCommand.ExecuteRequested += async (_, _) => await Monitor.RefreshAsync(force: true);

        var exitCommand = (XamlUICommand)Resources["ExitApplicationCommand"];
        exitCommand.ExecuteRequested += (_, _) => ExitApplication();

        TrayIcon = (TaskbarIcon)Resources["TrayIcon"];
        TrayIcon.ForceCreate();
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        try
        {
            await Monitor.RefreshAsync();
        }
        catch (Exception error)
        {
            CrashDiagnostics.Record("Scheduled usage refresh failed", error);
        }
    }

    private void Monitor_Changed(object? sender, EventArgs e)
    {
        dispatcherQueue?.TryEnqueue(UpdateTrayTooltip);
    }

    private void UpdateTrayTooltip()
    {
        if (TrayIcon is null)
        {
            return;
        }

        var state = Monitor.State;
        var limit = state.Snapshot?.Limits.OrderByDescending(item => item.WindowMinutes).FirstOrDefault();
        TrayIcon.ToolTipText = limit is not null
            ? $"Codex usage / {limit.Name} {limit.UsedPercent:0.0}% used / {limit.RemainingPercent:0.0}% left"
            : $"Codex usage / {state.Error ?? "waiting for local telemetry"}";
    }

    private void ShowWindow()
    {
        if (window is null)
        {
            window = new Window
            {
                Title = "Codex Decision",
                Content = new ShellPage(),
            };
            window.Closed += Window_Closed;
            ConfigureWindow(window);
            PositionWindow(window);
        }

        window.Show();
        window.Activate();
        isWindowVisible = true;
    }

    public void ShowTask(Guid projectId, Guid taskId)
    {
        ShowWindow();
        if (window?.Content is ShellPage shell)
        {
            shell.NavigateToTask(projectId, taskId);
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (isExiting)
        {
            return;
        }

        args.Handled = true;
        try
        {
            window?.Hide();
            isWindowVisible = false;
        }
        catch (Exception error)
        {
            CrashDiagnostics.Record("Window hide failed", error);
        }
    }

    private static void ConfigureWindow(Window target)
    {
        target.AppWindow.IsShownInSwitchers = true;
        if (target.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }
    }

    private static void PositionWindow(Window target)
    {
        const int preferredWidth = 1320;
        const int preferredHeight = 860;
        var display = DisplayArea.GetFromWindowId(target.AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var width = Math.Min(preferredWidth, Math.Max(900, workArea.Width - 64));
        var height = Math.Min(preferredHeight, Math.Max(620, workArea.Height - 64));
        target.AppWindow.MoveAndResize(
            new RectInt32(
                workArea.X + (workArea.Width - width) / 2,
                workArea.Y + (workArea.Height - height) / 2,
                width,
                height));
    }

    private async void ExitApplication()
    {
        try
        {
            isExiting = true;
            refreshTimer.Stop();
            signalCancellation.Cancel();
            showWindowSignal.Set();
            exitApplicationSignal.Set();
            TrayIcon?.Dispose();
            Services.TaskRuntimes.SnapshotChanged -= TaskRuntimes_SnapshotChanged;
            notifications?.Dispose();
            window?.Close();
            await Services.DisposeAsync();
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            showWindowSignal.Dispose();
            exitApplicationSignal.Dispose();
            signalCancellation.Dispose();
        }
        catch (Exception error)
        {
            CrashDiagnostics.Record("Application shutdown failed", error);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private void TaskRuntimes_SnapshotChanged(object? sender, CodexDecision.Core.Conversations.TaskRuntimeSnapshot snapshot)
    {
        notifications?.HandleSnapshot(snapshot);
    }
}
