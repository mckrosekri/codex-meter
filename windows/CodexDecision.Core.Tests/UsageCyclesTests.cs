using CodexDecision.Core.Usage;

namespace CodexDecision.Core.Tests;

public sealed class UsageCyclesTests
{
    [Fact]
    public void GroupsUsageIntoSubscriptionCyclesOnTheConfiguredRenewalDay()
    {
        var model = UsageCycles.Build(
            [
                Day("2026-06-22", 100),
                Day("2026-06-23", 200),
                Day("2026-07-22", 300),
                Day("2026-07-23", 400),
            ],
            23,
            new DateOnly(2026, 7, 24));

        Assert.Equal(UsageCycleMode.Subscription, model.Mode);
        Assert.Equal("2026-07-23", model.CurrentCycleKey);
        Assert.Equal(new DateOnly(2026, 7, 23), model.Cycles[0].StartDate);
        Assert.Equal(new DateOnly(2026, 8, 22), model.Cycles[0].EndDate);
        Assert.Equal(400, model.Cycles[0].Totals.TotalTokens);
        Assert.Equal(500, model.Cycles[1].Totals.TotalTokens);
        Assert.Equal(500, model.Cycles[1].Totals.Models["gpt-5.4"].TotalTokens);
        Assert.Equal(100, model.Cycles[2].Totals.TotalTokens);
    }

    [Fact]
    public void UsesIndependentCalendarMonthsWhileRenewalDayIsUnset()
    {
        var model = UsageCycles.Build(
            [Day("2026-06-30", 100), Day("2026-07-01", 200)],
            null,
            new DateOnly(2026, 7, 14));

        Assert.Equal(UsageCycleMode.Calendar, model.Mode);
        Assert.Equal("2026-07-01", model.CurrentCycleKey);
        Assert.Equal(200, model.Cycles[0].Totals.TotalTokens);
        Assert.Equal(100, model.Cycles[1].Totals.TotalTokens);
    }

    [Fact]
    public void ClampsRenewalDaysToTheEndOfShorterMonths()
    {
        var model = UsageCycles.Build(
            [Day("2027-02-27", 100), Day("2027-02-28", 200), Day("2027-03-30", 300)],
            31,
            new DateOnly(2027, 3, 30));

        Assert.Equal("2027-02-28", model.CurrentCycleKey);
        Assert.Equal(new DateOnly(2027, 3, 30), model.Cycles[0].EndDate);
        Assert.Equal(500, model.Cycles[0].Totals.TotalTokens);
        Assert.Equal(new DateOnly(2027, 2, 27), model.Cycles[1].EndDate);
        Assert.Equal(100, model.Cycles[1].Totals.TotalTokens);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(12, "12th")]
    [InlineData(23, "23rd")]
    public void ValidatesRenewalDaysAndFormatsOrdinals(int day, string expected)
    {
        Assert.Equal(day, UsageCycles.NormalizeBillingDay(day));
        Assert.Equal(expected, UsageCycles.Ordinal(day));
        Assert.Null(UsageCycles.NormalizeBillingDay(32));
    }

    private static DailyUsage Day(string date, long totalTokens)
    {
        var tokens = new TokenUsage(totalTokens - 10, 20, 10, 2, totalTokens);
        return new DailyUsage(
            DateOnly.Parse(date),
            new UsageAggregate(tokens, 1, new Dictionary<string, TokenUsage> { ["gpt-5.4"] = tokens }));
    }
}
