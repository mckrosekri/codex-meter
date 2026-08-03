using System.Collections.ObjectModel;
using System.Globalization;
using CodexDecision.Core.Usage;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Media;

namespace CodexMeterTray.Views;

public sealed class UsageLimitView
{
    public string Name { get; set; } = string.Empty;

    public string WindowLabel { get; set; } = string.Empty;

    public string UsedText { get; set; } = string.Empty;

    public string RemainingText { get; set; } = string.Empty;

    public string ResetRelative { get; set; } = string.Empty;

    public string ResetExact { get; set; } = string.Empty;

    public string AccessibleName { get; set; } = string.Empty;

    public double UsedPercent { get; set; }

    public Brush? ProgressBrush { get; set; }
}

public sealed class UsageCycleChoice
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public sealed class UsageChoice
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public sealed class UsageBarView
{
    public string Label { get; set; } = string.Empty;

    public string AccessibleName { get; set; } = string.Empty;

    public double Height { get; set; }
}

public partial class MainPage : Page
{
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;
    private string? selectedCycleKey;
    private bool isRendering;

    private UsageMonitor Monitor => ((App)Application.Current).Monitor;

    public MainPage()
    {
        InitializeComponent();
        PopulatePreferenceChoices();
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    public ObservableCollection<UsageLimitView> LimitRows { get; } = [];

    public ObservableCollection<UsageCycleChoice> CycleChoices { get; } = [];

    public ObservableCollection<UsageChoice> BillingDayChoices { get; } = [];

    public ObservableCollection<UsageChoice> PricingChoices { get; } = [];

    public ObservableCollection<UsageBarView> DailyBars { get; } = [];

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Monitor.Changed += Monitor_Changed;
        Render(Monitor.State);
        if (Monitor.State.Snapshot is null && !Monitor.State.IsRefreshing)
        {
            _ = Monitor.RefreshAsync();
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Monitor.Changed -= Monitor_Changed;
    }

    private void Monitor_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => Render(Monitor.State));
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await Monitor.RefreshAsync(force: true);
    }

    private async void BillingDayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || BillingDayCombo.SelectedItem is not UsageChoice choice)
        {
            return;
        }

        selectedCycleKey = null;
        await Monitor.SetBillingDayAsync(int.TryParse(choice.Value, out var day) ? day : null);
    }

    private async void PricingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || PricingCombo.SelectedItem is not UsageChoice choice)
        {
            return;
        }

        await Monitor.SetPricingBasisAsync(choice.Value);
    }

    private void CycleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || CycleCombo.SelectedItem is not UsageCycleChoice choice)
        {
            return;
        }

        selectedCycleKey = choice.Key;
        Render(Monitor.State);
    }

    private void PopulatePreferenceChoices()
    {
        BillingDayChoices.Add(new UsageChoice { Value = string.Empty, Label = "Renewal not set" });
        for (var day = 1; day <= 31; day++)
        {
            BillingDayChoices.Add(new UsageChoice { Value = day.ToString(), Label = UsageCycles.Ordinal(day) });
        }

        BillingDayCombo.ItemsSource = BillingDayChoices;
        PricingChoices.Add(new UsageChoice { Value = "auto", Label = "Auto / recorded models" });
        foreach (var model in UsagePricing.Models)
        {
            PricingChoices.Add(new UsageChoice { Value = model.Id, Label = model.Label });
        }

        PricingCombo.ItemsSource = PricingChoices;
    }

    private void Render(UsageState state)
    {
        isRendering = true;
        try
        {
            RefreshButton.IsEnabled = !state.IsRefreshing;
            ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(state.Error);
            ErrorInfoBar.Message = state.Error ?? string.Empty;
            if (state.Snapshot is not { } snapshot)
            {
                UpdatedText.Text = state.IsRefreshing ? "Reading local Codex telemetry" : "No usage snapshot available";
                RenderIndexing(null, state.IsRefreshing);
                LimitRows.Clear();
                LimitsEmptyPanel.Visibility = Visibility.Visible;
                return;
            }

            UpdatedText.Text = $"Updated {RelativeTime(snapshot.GeneratedAt)} / {FormatNumber(snapshot.Activity.AllTime.TotalTokens)} tokens indexed in total";
            PlanChip.Visibility = string.IsNullOrWhiteSpace(snapshot.Account.Plan) ? Visibility.Collapsed : Visibility.Visible;
            PlanText.Text = string.IsNullOrWhiteSpace(snapshot.Account.Plan) ? string.Empty : $"{snapshot.Account.Plan!.ToUpperInvariant()} PLAN";
            RenderIndexing(snapshot.Source.Scan, state.IsRefreshing);
            RenderLimits(snapshot.Limits);
            RenderActivity(snapshot, state.Preferences);
            PrivacyText.Text = $"Local only. Reads token-count events from {snapshot.Source.Label}; prompt content is never stored or shown.";
            HistoryText.Text = snapshot.Source.FirstEventAt is { } first
                ? $"{FormatNumber(snapshot.Source.FilesIndexed)} sessions / history since {first.ToLocalTime():d MMM}"
                : $"{FormatNumber(snapshot.Source.FilesIndexed)} sessions indexed";
        }
        finally
        {
            isRendering = false;
        }
    }

    private void RenderIndexing(UsageScanStatus? scan, bool refreshing)
    {
        if (scan is null)
        {
            IndexingPanel.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
            IndexingTitle.Text = "Indexing local history";
            IndexingDetail.Text = "Current limits appear as soon as recent telemetry is found.";
            IndexingPercent.Text = string.Empty;
            IndexingProgress.IsIndeterminate = refreshing;
            return;
        }

        var visible = scan.Phase is UsageScanPhase.Starting or UsageScanPhase.Indexing or UsageScanPhase.Error;
        IndexingPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        IndexingProgress.IsIndeterminate = scan.Phase is UsageScanPhase.Starting;
        IndexingProgress.Value = scan.Percent;
        IndexingPercent.Text = $"{scan.Percent}%";
        if (scan.Phase is UsageScanPhase.Error)
        {
            IndexingTitle.Text = "History index stopped";
            IndexingDetail.Text = scan.Error ?? "An unknown indexing error occurred.";
        }
        else
        {
            IndexingTitle.Text = $"Indexing local history / {scan.Percent}%";
            IndexingDetail.Text = $"{FormatNumber(scan.FilesProcessed)} of {FormatNumber(scan.FilesFound)} session files. Current limits are already available.";
        }
    }

    private void RenderLimits(IReadOnlyList<UsageLimitSnapshot> limits)
    {
        LimitRows.Clear();
        foreach (var limit in limits)
        {
            var brush = (Brush)Application.Current.Resources[limit.State switch
            {
                UsageLimitState.Critical => "AppDangerBrush",
                UsageLimitState.Warning => "AppWarningBrush",
                _ => "AppGoodBrush",
            }];
            LimitRows.Add(new UsageLimitView
            {
                Name = limit.Name,
                WindowLabel = WindowLabel(limit.WindowMinutes),
                UsedText = $"{limit.UsedPercent:0.0}% used",
                RemainingText = $"{limit.RemainingPercent:0.0}% remaining",
                ResetRelative = limit.ResetsAt is { } reset ? $"Resets {RelativeTime(reset)}" : "Reset unavailable",
                ResetExact = limit.ResetsAt?.ToLocalTime().ToString("g", culture) ?? string.Empty,
                AccessibleName = $"{limit.Name}: {limit.UsedPercent:0.0}% used",
                UsedPercent = Math.Clamp(limit.UsedPercent, 0, 100),
                ProgressBrush = brush,
            });
        }

        LimitsEmptyPanel.Visibility = limits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderActivity(UsageSnapshot snapshot, UsagePreferences preferences)
    {
        var localNow = DateOnly.FromDateTime(snapshot.GeneratedAt.ToLocalTime().DateTime);
        var model = UsageCycles.Build(snapshot.Activity.Daily, preferences.BillingDay, localNow);
        if (model.Cycles.All(cycle => cycle.Key != selectedCycleKey))
        {
            selectedCycleKey = model.CurrentCycleKey;
        }

        var cycle = model.Cycles.FirstOrDefault(candidate => candidate.Key == selectedCycleKey) ?? model.Cycles[0];
        CycleChoices.Clear();
        foreach (var candidate in model.Cycles)
        {
            CycleChoices.Add(new UsageCycleChoice
            {
                Key = candidate.Key,
                Label = $"{CycleLabel(candidate, model.Mode)}{(candidate.IsCurrent ? " / current" : string.Empty)}",
            });
        }

        CycleCombo.ItemsSource = CycleChoices;
        CycleCombo.SelectedItem = CycleChoices.FirstOrDefault(choice => choice.Key == cycle.Key);
        BillingDayCombo.SelectedItem = BillingDayChoices.First(choice => choice.Value == (preferences.BillingDay?.ToString() ?? string.Empty));
        PricingCombo.SelectedItem = PricingChoices.First(choice => choice.Value == UsagePricing.NormalizeBasis(preferences.PricingBasis));
        ActivityScopeText.Text = preferences.BillingDay is { } day
            ? $"Subscription-cycle totals / renews on the {UsageCycles.Ordinal(day)}."
            : "Calendar-month totals / set your renewal day to match your subscription cycle.";

        var totals = cycle.Totals;
        TotalTokensText.Text = FormatNumber(totals.TotalTokens);
        InputTokensText.Text = FormatNumber(totals.InputTokens);
        OutputTokensText.Text = FormatNumber(totals.OutputTokens);
        ResponsesText.Text = FormatNumber(totals.Requests);
        UncachedInputText.Text = FormatNumber(Math.Max(0, totals.InputTokens - totals.CachedInputTokens));
        CachedInputText.Text = FormatNumber(totals.CachedInputTokens);
        BreakdownOutputText.Text = FormatNumber(totals.OutputTokens);
        ReasoningOutputText.Text = FormatNumber(totals.ReasoningOutputTokens);

        var estimate = UsagePricing.Estimate(totals, preferences.PricingBasis);
        CostText.Text = estimate.Cost.Total.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        if (estimate.Basis != "auto")
        {
            CostContextText.Text = $"Using {estimate.Label} standard API rates.";
        }
        else
        {
            var modelText = $"{estimate.RecordedModels} recorded model{(estimate.RecordedModels == 1 ? string.Empty : "s")}";
            CostContextText.Text = estimate.FallbackModels.Count > 0
                ? $"{modelText} / {estimate.FallbackModels.Count} unpriced model{(estimate.FallbackModels.Count == 1 ? string.Empty : "s")} used the GPT-5.5 fallback."
                : $"Automatically priced from {modelText}.";
        }

        RenderChart(cycle);
    }

    private void RenderChart(UsageCycle cycle)
    {
        DailyBars.Clear();
        var maximum = cycle.Daily.Count == 0 ? 0 : cycle.Daily.Max(day => day.Totals.TotalTokens);
        var total = cycle.Daily.Sum(day => day.Totals.TotalTokens);
        ChartTotalText.Text = $"{FormatNumber(total)} total";
        ChartEmptyText.Visibility = maximum == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (maximum == 0)
        {
            return;
        }

        var labelEvery = cycle.Daily.Count <= 7 ? 1 : cycle.Daily.Count <= 14 ? 2 : 5;
        for (var index = 0; index < cycle.Daily.Count; index++)
        {
            var day = cycle.Daily[index];
            var height = day.Totals.TotalTokens == 0
                ? 2
                : Math.Max(5, (double)day.Totals.TotalTokens / maximum * 174);
            var showLabel = index % labelEvery == 0 || index == cycle.Daily.Count - 1;
            DailyBars.Add(new UsageBarView
            {
                Height = height,
                Label = showLabel ? day.Date.ToString("MMM d", culture) : string.Empty,
                AccessibleName = $"{day.Date.ToString("D", culture)}: {FormatNumber(day.Totals.TotalTokens)} tokens, {FormatNumber(day.Totals.Requests)} model responses",
            });
        }
    }

    private string FormatNumber(long value) => value.ToString("N0", culture);

    private static string WindowLabel(int minutes)
    {
        if (minutes <= 0) return "Window unavailable";
        if (minutes % 1_440 == 0) return $"{minutes / 1_440}-day window";
        if (minutes % 60 == 0) return $"{minutes / 60}-hour window";
        return $"{minutes}-minute window";
    }

    private static string RelativeTime(DateTimeOffset value)
    {
        var difference = value - DateTimeOffset.Now;
        var absolute = difference.Duration();
        if (absolute >= TimeSpan.FromDays(1))
        {
            return RelativeUnit((int)Math.Round(difference.TotalDays), "day");
        }

        if (absolute >= TimeSpan.FromHours(1))
        {
            return RelativeUnit((int)Math.Round(difference.TotalHours), "hour");
        }

        return RelativeUnit((int)Math.Round(difference.TotalMinutes), "minute");
    }

    private static string RelativeUnit(int value, string unit)
    {
        var plural = Math.Abs(value) == 1 ? string.Empty : "s";
        return value >= 0 ? $"in {value} {unit}{plural}" : $"{Math.Abs(value)} {unit}{plural} ago";
    }

    private static string CycleLabel(UsageCycle cycle, UsageCycleMode mode)
    {
        return mode is UsageCycleMode.Calendar
            ? cycle.StartDate.ToString("MMMM yyyy")
            : $"{cycle.StartDate:MMM d, yyyy} - {cycle.EndDate:MMM d, yyyy}";
    }
}
