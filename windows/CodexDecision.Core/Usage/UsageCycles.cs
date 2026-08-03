namespace CodexDecision.Core.Usage;

public static class UsageCycles
{
    public static int? NormalizeBillingDay(int? value)
    {
        return value is >= 1 and <= 31 ? value : null;
    }

    public static string Ordinal(int day)
    {
        var mod100 = day % 100;
        if (mod100 is >= 11 and <= 13)
        {
            return $"{day}th";
        }

        return (day % 10) switch
        {
            1 => $"{day}st",
            2 => $"{day}nd",
            3 => $"{day}rd",
            _ => $"{day}th",
        };
    }

    public static UsageCycleModel Build(
        IReadOnlyList<DailyUsage>? daily,
        int? billingDayValue,
        DateOnly now)
    {
        var billingDay = NormalizeBillingDay(billingDayValue);
        var effectiveDay = billingDay ?? 1;
        var currentBounds = CycleBounds(now, effectiveDay);
        var values = new Dictionary<DateOnly, MutableAggregate>();

        foreach (var item in daily ?? [])
        {
            if (!values.TryGetValue(item.Date, out var value))
            {
                value = new MutableAggregate();
                values[item.Date] = value;
            }

            value.Add(item.Totals);
        }

        var cycleMap = new Dictionary<string, MutableCycle>();
        foreach (var (date, totals) in values)
        {
            var bounds = CycleBounds(date, effectiveDay);
            if (!cycleMap.TryGetValue(bounds.Key, out var cycle))
            {
                cycle = new MutableCycle(bounds);
                cycleMap[bounds.Key] = cycle;
            }

            cycle.Totals.Add(totals.ToImmutable());
            cycle.Values[date] = totals;
        }

        if (!cycleMap.ContainsKey(currentBounds.Key))
        {
            cycleMap[currentBounds.Key] = new MutableCycle(currentBounds);
        }

        var earliestKey = cycleMap.Keys.Order(StringComparer.Ordinal).First();
        var cursorBounds = currentBounds;
        while (string.CompareOrdinal(cursorBounds.Key, earliestKey) > 0)
        {
            cursorBounds = CycleBounds(cursorBounds.Start.AddDays(-1), effectiveDay);
            cycleMap.TryAdd(cursorBounds.Key, new MutableCycle(cursorBounds));
        }

        var cycles = cycleMap.Values
            .OrderByDescending(cycle => cycle.Bounds.Key, StringComparer.Ordinal)
            .Select(cycle =>
            {
                var isCurrent = cycle.Bounds.Key == currentBounds.Key;
                var chartEnd = isCurrent && now < cycle.Bounds.End ? now : cycle.Bounds.End;
                var series = new List<DailyUsage>();
                for (var date = cycle.Bounds.Start; date <= chartEnd; date = date.AddDays(1))
                {
                    series.Add(new DailyUsage(
                        date,
                        cycle.Values.TryGetValue(date, out var value)
                            ? value.ToImmutable()
                            : UsageAggregate.Empty));
                }

                return new UsageCycle(
                    cycle.Bounds.Key,
                    cycle.Bounds.Start,
                    cycle.Bounds.End,
                    isCurrent,
                    cycle.Totals.ToImmutable(),
                    series);
            })
            .ToArray();

        return new UsageCycleModel(
            billingDay is null ? UsageCycleMode.Calendar : UsageCycleMode.Subscription,
            billingDay,
            currentBounds.Key,
            cycles);
    }

    private static CycleRange CycleBounds(DateOnly date, int billingDay)
    {
        var monthStart = new DateOnly(date.Year, date.Month, 1);
        var effectiveDay = Math.Min(billingDay, DateTime.DaysInMonth(date.Year, date.Month));
        if (date.Day < effectiveDay)
        {
            monthStart = monthStart.AddMonths(-1);
        }

        var start = new DateOnly(
            monthStart.Year,
            monthStart.Month,
            Math.Min(billingDay, DateTime.DaysInMonth(monthStart.Year, monthStart.Month)));
        var nextMonth = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
        var next = new DateOnly(
            nextMonth.Year,
            nextMonth.Month,
            Math.Min(billingDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month)));
        return new CycleRange(start.ToString("yyyy-MM-dd"), start, next.AddDays(-1));
    }

    private sealed record CycleRange(string Key, DateOnly Start, DateOnly End);

    private sealed class MutableCycle(CycleRange bounds)
    {
        public CycleRange Bounds { get; } = bounds;

        public MutableAggregate Totals { get; } = new();

        public Dictionary<DateOnly, MutableAggregate> Values { get; } = [];
    }

    private sealed class MutableAggregate
    {
        private TokenUsage tokens = TokenUsage.Empty;
        private long requests;
        private readonly Dictionary<string, TokenUsage> models = new(StringComparer.OrdinalIgnoreCase);

        public void Add(UsageAggregate source)
        {
            tokens = AddTokens(tokens, source.Tokens);
            requests += source.Requests;
            foreach (var (model, value) in source.Models)
            {
                models[model] = AddTokens(models.GetValueOrDefault(model, TokenUsage.Empty), value);
            }
        }

        public UsageAggregate ToImmutable()
        {
            return new UsageAggregate(tokens, requests, new Dictionary<string, TokenUsage>(models));
        }
    }

    internal static TokenUsage AddTokens(TokenUsage left, TokenUsage right)
    {
        return new TokenUsage(
            left.InputTokens + right.InputTokens,
            left.CachedInputTokens + right.CachedInputTokens,
            left.OutputTokens + right.OutputTokens,
            left.ReasoningOutputTokens + right.ReasoningOutputTokens,
            left.TotalTokens + right.TotalTokens);
    }
}
