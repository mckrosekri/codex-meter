using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CodexDecision.Core.Git;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMeterTray.Views;

public sealed class GitDiffLineView
{
    public GitDiffLineView(string oldLine, string newLine, string text, Brush background, Brush foreground)
    {
        OldLine = oldLine;
        NewLine = newLine;
        Text = text;
        Background = background;
        Foreground = foreground;
    }

    public string OldLine { get; set; }

    public string NewLine { get; set; }

    public string Text { get; set; }

    public Brush Background { get; set; }

    public Brush Foreground { get; set; }
}

public sealed partial class GitReviewDialog : ContentDialog
{
    private readonly GitWorktreeService worktrees;
    private readonly string workingDirectory;
    private readonly bool compareBranches;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? diffCancellation;
    private GitReviewSnapshot? snapshot;
    private bool hasLoaded;
    private bool isPopulatingBranches;

    public GitReviewDialog(
        GitWorktreeService worktrees,
        string workingDirectory,
        bool compareBranches = false)
    {
        this.worktrees = worktrees;
        this.workingDirectory = workingDirectory;
        this.compareBranches = compareBranches;
        InitializeComponent();
        if (compareBranches)
        {
            Title = "Compare branch";
            BaseBranchComboBox.Visibility = Visibility.Visible;
        }
        Resources["ContentDialogMaxWidth"] = 1200d;
        Resources["ContentDialogMinWidth"] = 320d;
        FilesList.ItemsSource = Files;
        Opened += GitReviewDialog_Opened;
        Closed += GitReviewDialog_Closed;
        ReviewRoot.SizeChanged += ReviewRoot_SizeChanged;
    }

    public ObservableCollection<GitReviewFile> Files { get; } = [];

    private async void GitReviewDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        SizeForHost();
        if (!hasLoaded)
        {
            hasLoaded = true;
            if (compareBranches && !await LoadBranchesAsync())
            {
                return;
            }

            await RefreshAsync();
        }
    }

    private void GitReviewDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        diffCancellation?.Cancel();
        lifetimeCancellation.Cancel();
    }

    private void SizeForHost()
    {
        if (XamlRoot?.Content is not FrameworkElement host)
        {
            return;
        }

        ReviewRoot.Width = Math.Min(1080, Math.Max(320, host.ActualWidth - 80));
        ReviewRoot.Height = Math.Min(680, Math.Max(420, host.ActualHeight - 160));
        ApplyResponsiveLayout(ReviewRoot.Width);
    }

    private void ReviewRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var stacked = width < 720;
        if (stacked)
        {
            FileColumn.Width = new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            DiffColumn.Width = new GridLength(0);
            BodyGrid.RowDefinitions[0].Height = new GridLength(190);
            BodyGrid.RowDefinitions[1].Height = new GridLength(1);
            BodyGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(FilePane, 0);
            Grid.SetColumn(FilePane, 0);
            Grid.SetRow(PaneDivider, 1);
            Grid.SetColumn(PaneDivider, 0);
            Grid.SetRow(DiffPane, 2);
            Grid.SetColumn(DiffPane, 0);
            PaneDivider.Width = double.NaN;
            PaneDivider.Height = 1;
            PaneDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
            PaneDivider.VerticalAlignment = VerticalAlignment.Center;
            FilePane.Padding = new Thickness(0, 10, 0, 8);
            DiffPane.Padding = new Thickness(0, 10, 0, 0);
            return;
        }

        FileColumn.Width = new GridLength(300);
        DividerColumn.Width = new GridLength(1);
        DiffColumn.Width = new GridLength(1, GridUnitType.Star);
        BodyGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        BodyGrid.RowDefinitions[1].Height = new GridLength(0);
        BodyGrid.RowDefinitions[2].Height = new GridLength(0);
        Grid.SetRow(FilePane, 0);
        Grid.SetColumn(FilePane, 0);
        Grid.SetRow(PaneDivider, 0);
        Grid.SetColumn(PaneDivider, 1);
        Grid.SetRow(DiffPane, 0);
        Grid.SetColumn(DiffPane, 2);
        PaneDivider.Width = 1;
        PaneDivider.Height = double.NaN;
        PaneDivider.HorizontalAlignment = HorizontalAlignment.Center;
        PaneDivider.VerticalAlignment = VerticalAlignment.Stretch;
        FilePane.Padding = new Thickness(0, 10, 10, 0);
        DiffPane.Padding = new Thickness(12, 10, 0, 0);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void BaseBranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isPopulatingBranches && hasLoaded && BaseBranchComboBox.SelectedItem is string)
        {
            await RefreshAsync();
        }
    }

    private async Task<bool> LoadBranchesAsync()
    {
        SetRepositoryLoading(true);
        ReviewError.IsOpen = false;
        try
        {
            var branches = await worktrees.GetBranchesAsync(workingDirectory, lifetimeCancellation.Token);
            var candidates = branches.ToArray();
            isPopulatingBranches = true;
            BaseBranchComboBox.ItemsSource = candidates;
            BaseBranchComboBox.SelectedItem = candidates.FirstOrDefault(branch => branch is "origin/main" or "origin/master")
                                               ?? candidates.FirstOrDefault(branch => branch is "main" or "master")
                                               ?? candidates.FirstOrDefault();
            isPopulatingBranches = false;
            if (BaseBranchComboBox.SelectedItem is not string)
            {
                SummaryText.Text = "No other branch is available";
                ReviewError.Message = "Create or fetch another branch before comparing this checkout.";
                ReviewError.IsOpen = true;
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            SummaryText.Text = "Unable to load branches";
            ReviewError.Message = error.Message;
            ReviewError.IsOpen = true;
            return false;
        }
        finally
        {
            isPopulatingBranches = false;
            SetRepositoryLoading(false);
        }
    }

    private async Task RefreshAsync()
    {
        var selectedPath = (FilesList.SelectedItem as GitReviewFile)?.Path;
        SetRepositoryLoading(true);
        ReviewError.IsOpen = false;
        try
        {
            var baseBranch = BaseBranchComboBox.SelectedItem as string;
            snapshot = compareBranches
                ? await worktrees.GetBranchReviewAsync(
                    workingDirectory,
                    baseBranch ?? throw new InvalidOperationException("Select a branch to compare."),
                    lifetimeCancellation.Token)
                : await worktrees.GetReviewAsync(workingDirectory, lifetimeCancellation.Token);
            Files.Clear();
            foreach (var file in snapshot.Files)
            {
                Files.Add(file);
            }

            var countLabel = Files.Count == 1 ? "1 changed file" : $"{Files.Count} changed files";
            SummaryText.Text = Files.Count == 0
                ? compareBranches ? $"No changes from {baseBranch}" : "Working tree is clean"
                : $"{countLabel}  ·  +{snapshot.TotalAdditions}  -{snapshot.TotalDeletions}";
            var currentBranch = string.IsNullOrWhiteSpace(snapshot.BranchName)
                ? "Detached HEAD"
                : snapshot.BranchName;
            BranchText.Text = compareBranches ? $"{baseBranch} ↔ {currentBranch}" : currentBranch;
            RepositoryText.Text = snapshot.RepositoryRoot;
            AutomationProperties.SetName(FilesList, countLabel);

            var selected = Files.FirstOrDefault(file =>
                               string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                           ?? Files.FirstOrDefault();
            FilesList.SelectedItem = selected;
            if (selected is null)
            {
                SelectedFileText.Text = "No changed files";
                SelectedFileMetaText.Text = compareBranches
                    ? $"The current branch has no committed differences from {baseBranch}."
                    : "The working tree has no staged, unstaged, or untracked files.";
                DiffLinesList.ItemsSource = new[]
                {
                    CreateMessageLine(compareBranches ? "The branches have no file differences." : "Working tree is clean."),
                };
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SummaryText.Text = "Unable to load Git changes";
            ReviewError.Message = error.Message;
            ReviewError.IsOpen = true;
        }
        finally
        {
            SetRepositoryLoading(false);
        }
    }

    private async void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (snapshot is null || FilesList.SelectedItem is not GitReviewFile file)
        {
            return;
        }

        diffCancellation?.Cancel();
        diffCancellation?.Dispose();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        diffCancellation = requestCancellation;
        SelectedFileText.Text = file.Path;
        SelectedFileMetaText.Text = BuildFileSummary(file);
        AutomationProperties.SetName(DiffLinesList, $"Git diff for {file.Path}");
        SetDiffLoading(true);
        try
        {
            var diff = compareBranches
                ? await worktrees.GetBranchFileDiffAsync(
                    snapshot.RepositoryRoot,
                    BaseBranchComboBox.SelectedItem as string
                    ?? throw new InvalidOperationException("Select a branch to compare."),
                    file.Path,
                    requestCancellation.Token)
                : await worktrees.GetFileDiffAsync(
                    snapshot.RepositoryRoot,
                    file.Path,
                    file.IsUntracked,
                    requestCancellation.Token);
            if (ReferenceEquals(FilesList.SelectedItem, file))
            {
                var lines = ParseDiffLines(diff);
                DiffLinesList.ItemsSource = lines;
                if (lines.Count > 0)
                {
                    DiffLinesList.ScrollIntoView(lines[0]);
                }
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ReviewError.Message = error.Message;
            ReviewError.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(diffCancellation, requestCancellation))
            {
                SetDiffLoading(false);
            }
        }
    }

    private static string BuildFileSummary(GitReviewFile file)
    {
        var statistics = file.Additions is null || file.Deletions is null
            ? string.Empty
            : $"  ·  +{file.Additions}  -{file.Deletions}";
        return $"{file.StatusLabel}  ·  {file.StageLabel}{statistics}";
    }

    private static IReadOnlyList<GitDiffLineView> ParseDiffLines(string diff)
    {
        var normalBackground = ResourceBrush("AppBackgroundBrush");
        var normalText = ResourceBrush("AppTextBrush");
        var mutedText = ResourceBrush("AppMutedBrush");
        var additionBackground = ResourceBrush("GitAdditionLineBrush");
        var deletionBackground = ResourceBrush("GitDeletionLineBrush");
        var hunkBackground = ResourceBrush("GitHunkLineBrush");
        var hunkText = ResourceBrush("GitHunkTextBrush");
        var additionText = ResourceBrush("AppGoodBrush");
        var deletionText = ResourceBrush("AppDangerBrush");
        var lines = new List<GitDiffLineView>();
        int? oldLine = null;
        int? newLine = null;

        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var hunk = Regex.Match(line, "^@@ -(?<old>\\d+)(?:,\\d+)? \\+(?<new>\\d+)(?:,\\d+)? @@");
            if (hunk.Success)
            {
                oldLine = int.Parse(hunk.Groups["old"].Value);
                newLine = int.Parse(hunk.Groups["new"].Value);
                lines.Add(new GitDiffLineView(string.Empty, string.Empty, line, hunkBackground, hunkText));
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                lines.Add(new GitDiffLineView(
                    string.Empty,
                    LineNumber(newLine),
                    line,
                    additionBackground,
                    additionText));
                newLine++;
                continue;
            }

            if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                lines.Add(new GitDiffLineView(
                    LineNumber(oldLine),
                    string.Empty,
                    line,
                    deletionBackground,
                    deletionText));
                oldLine++;
                continue;
            }

            if (line.StartsWith(' ') && oldLine is not null && newLine is not null)
            {
                lines.Add(new GitDiffLineView(
                    LineNumber(oldLine),
                    LineNumber(newLine),
                    line,
                    normalBackground,
                    normalText));
                oldLine++;
                newLine++;
                continue;
            }

            lines.Add(new GitDiffLineView(string.Empty, string.Empty, line, normalBackground, mutedText));
        }

        return lines;
    }

    private static GitDiffLineView CreateMessageLine(string message) =>
        new(string.Empty, string.Empty, message, ResourceBrush("AppBackgroundBrush"), ResourceBrush("AppMutedBrush"));

    private static string LineNumber(int? value) => value?.ToString() ?? string.Empty;

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

    private void SetRepositoryLoading(bool loading)
    {
        RepositoryLoadingRing.IsActive = loading;
        RepositoryLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDiffLoading(bool loading)
    {
        DiffLoadingRing.IsActive = loading;
        DiffLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }
}
