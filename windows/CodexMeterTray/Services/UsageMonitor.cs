using CodexDecision.Core.Usage;

namespace CodexMeterTray.Services;

public sealed record UsageState(
    UsageSnapshot? Snapshot,
    UsagePreferences Preferences,
    string? Error,
    bool IsRefreshing);

public sealed class UsageMonitor
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim preferencesGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly CodexUsageStore store;
    private readonly UsagePreferencesStore preferencesStore;
    private UsageState state = new(null, UsagePreferences.Default, null, false);
    private bool preferencesLoaded;

    public UsageMonitor(
        CodexUsageStore? store = null,
        UsagePreferencesStore? preferencesStore = null)
    {
        this.store = store ?? new CodexUsageStore();
        this.preferencesStore = preferencesStore ?? new UsagePreferencesStore();
    }

    public event EventHandler? Changed;

    public UsageState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await EnsurePreferencesLoadedAsync();
            SetState(State with { IsRefreshing = true, Error = null });
            var snapshot = await Task.Run(async () => await store.RefreshAsync(
                force,
                progress => SetState(new UsageState(progress, State.Preferences, null, true))));
            SetState(new UsageState(snapshot, State.Preferences, null, false));
        }
        catch (Exception error)
        {
            SetState(State with
            {
                Error = $"Refresh failed: {error.Message}",
                IsRefreshing = false,
            });
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task SetBillingDayAsync(int? billingDay)
    {
        await EnsurePreferencesLoadedAsync();
        await SavePreferencesAsync(State.Preferences with
        {
            BillingDay = UsageCycles.NormalizeBillingDay(billingDay),
        });
    }

    public async Task SetPricingBasisAsync(string? pricingBasis)
    {
        await EnsurePreferencesLoadedAsync();
        await SavePreferencesAsync(State.Preferences with
        {
            PricingBasis = UsagePricing.NormalizeBasis(pricingBasis),
        });
    }

    private async Task EnsurePreferencesLoadedAsync()
    {
        if (preferencesLoaded)
        {
            return;
        }

        await preferencesGate.WaitAsync();
        try
        {
            if (preferencesLoaded)
            {
                return;
            }

            var preferences = await preferencesStore.LoadAsync();
            preferencesLoaded = true;
            SetState(State with { Preferences = preferences });
        }
        finally
        {
            preferencesGate.Release();
        }
    }

    private async Task SavePreferencesAsync(UsagePreferences preferences)
    {
        await preferencesGate.WaitAsync();
        try
        {
            await preferencesStore.SaveAsync(preferences);
            SetState(State with { Preferences = preferences });
        }
        finally
        {
            preferencesGate.Release();
        }
    }

    private void SetState(UsageState value)
    {
        lock (stateGate)
        {
            state = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
