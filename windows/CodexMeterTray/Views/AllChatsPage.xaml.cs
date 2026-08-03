using System.Collections.ObjectModel;
using CodexDecision.Core.Conversations;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMeterTray.Views;

public sealed class ChatIndexRow
{
    public string ThreadId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Preview { get; set; } = string.Empty;

    public string Metadata { get; set; } = string.Empty;

    public string Placement { get; set; } = string.Empty;

    public string Updated { get; set; } = string.Empty;

    public string AttentionText { get; set; } = string.Empty;

    public Brush? AttentionBrush { get; set; }

    public Visibility AttentionVisibility { get; set; } = Visibility.Collapsed;

    public CodexThreadIndexEntry Entry { get; set; } = null!;
}

public sealed class TaskNavigationRequestedEventArgs(Guid projectId, Guid taskId) : EventArgs
{
    public Guid ProjectId { get; } = projectId;

    public Guid TaskId { get; } = taskId;
}

public partial class AllChatsPage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;
    private string searchQuery = string.Empty;

    public AllChatsPage()
    {
        InitializeComponent();
        Loaded += AllChatsPage_Loaded;
        Unloaded += AllChatsPage_Unloaded;
    }

    public event EventHandler<TaskNavigationRequestedEventArgs>? TaskOpenRequested;

    public ObservableCollection<ChatIndexRow> Rows { get; } = [];

    private void AllChatsPage_Loaded(object sender, RoutedEventArgs e)
    {
        services.Changed += Services_Changed;
        RenderRows();
    }

    private void AllChatsPage_Unloaded(object sender, RoutedEventArgs e) =>
        services.Changed -= Services_Changed;

    private void Services_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RenderRows);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await services.RefreshThreadIndexAsync();
            RenderRows();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("The Codex chat index could not refresh", error.Message);
        }
    }

    private void ArchivedToggle_Click(object sender, RoutedEventArgs e) => RenderRows();

    private void ChatSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason is not AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        searchQuery = sender.Text.Trim();
        RenderRows();
    }

    private async void ChatList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ChatIndexRow row)
        {
            return;
        }

        await OpenAsync(row.Entry);
    }

    private async Task OpenAsync(CodexThreadIndexEntry entry)
    {
        try
        {
            if (entry.IsArchived)
            {
                await ShowMessageAsync(
                    "This chat is archived",
                    "Unarchive it in Codex, refresh this index, and then open it here.");
                return;
            }

            var indexed = await services.EnsureIndexedThreadAsync(entry.ThreadId);
            TaskOpenRequested?.Invoke(
                this,
                new TaskNavigationRequestedEventArgs(indexed.ProjectId!.Value, indexed.TaskId!.Value));
        }
        catch (Exception error)
        {
            await ShowMessageAsync("This Codex chat could not be opened", error.Message);
        }
    }

    private void RenderRows()
    {
        Rows.Clear();
        var showArchived = ArchivedToggle.IsChecked is true;
        foreach (var entry in services.ThreadIndex.Where(entry =>
                     (showArchived || !entry.IsArchived) && Matches(entry)))
        {
            var attention = entry.TaskId is { } taskId
                ? services.TaskAttention.GetAttention(taskId)
                : TaskAttentionKind.None;
            Rows.Add(new ChatIndexRow
            {
                ThreadId = entry.ThreadId,
                Title = entry.Title,
                Preview = string.IsNullOrWhiteSpace(entry.Preview) ? "No preview available" : entry.Preview,
                Metadata = $"{entry.WorkingDirectory}  ·  {entry.ThreadId}",
                Placement = entry.IsArchived
                    ? "Archived"
                    : entry.ProjectName is { Length: > 0 } projectName
                        ? projectName
                        : "Indexing",
                Updated = RelativeTime(entry.UpdatedAt),
                AttentionText = attention switch
                {
                    TaskAttentionKind.Ready => "Task finished and is ready for review",
                    TaskAttentionKind.Failed => "Task stopped and needs review",
                    _ => string.Empty,
                },
                AttentionBrush = attention is TaskAttentionKind.Failed
                    ? (Brush)Application.Current.Resources["AppDangerBrush"]
                    : (Brush)Application.Current.Resources["AppPrimaryBrush"],
                AttentionVisibility = attention is TaskAttentionKind.None
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                Entry = entry,
            });
        }

        var activeCount = services.ThreadIndex.Count(entry => !entry.IsArchived);
        var archivedCount = services.ThreadIndex.Count(entry => entry.IsArchived);
        IndexSummaryText.Text = $"{activeCount:N0} active chats indexed from local Codex" +
                                (archivedCount > 0 ? $"  ·  {archivedCount:N0} archived" : string.Empty);
        EmptyState.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool Matches(CodexThreadIndexEntry entry) =>
        string.IsNullOrWhiteSpace(searchQuery) ||
        entry.Title.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase) ||
        entry.Preview.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase) ||
        entry.WorkingDirectory.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase) ||
        (entry.ProjectName?.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static string RelativeTime(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;
        return age.TotalMinutes < 1 ? "Just now"
            : age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)}m ago"
            : age.TotalDays < 1 ? $"{Math.Max(1, (int)age.TotalHours)}h ago"
            : age.TotalDays < 14 ? $"{Math.Max(1, (int)age.TotalDays)}d ago"
            : value.LocalDateTime.ToString("d MMM yyyy");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }
}
