using CodexDecision.Core.Usage;

namespace CodexDecision.Core.Tests;

public sealed class UsagePricingTests
{
    [Fact]
    public void PricesUncachedInputCachedInputAndOutputIndependently()
    {
        var estimate = UsagePricing.Estimate(
            Aggregate(new TokenUsage(2_000_000, 1_000_000, 500_000, 0, 2_500_000)),
            "gpt-5.4");

        Assert.Equal(2.5m, estimate.Cost.UncachedInput);
        Assert.Equal(0.25m, estimate.Cost.CachedInput);
        Assert.Equal(7.5m, estimate.Cost.Output);
        Assert.Equal(10.25m, estimate.Cost.Total);
    }

    [Fact]
    public void AutomaticallyUsesTheRecordedModelMix()
    {
        var estimate = UsagePricing.Estimate(new UsageAggregate(
            TokenUsage.Empty,
            2,
            new Dictionary<string, TokenUsage>
            {
                ["gpt-5.4"] = new(1_000_000, 0, 0, 0, 1_000_000),
                ["gpt-5.6-sol"] = new(0, 0, 1_000_000, 0, 1_000_000),
            }));

        Assert.Equal(32.5m, estimate.Cost.Total);
        Assert.Equal(2, estimate.RecordedModels);
        Assert.Empty(estimate.FallbackModels);
    }

    [Fact]
    public void UsesADisclosedFallbackForFutureUnpricedModels()
    {
        var estimate = UsagePricing.Estimate(new UsageAggregate(
            TokenUsage.Empty,
            1,
            new Dictionary<string, TokenUsage>
            {
                ["future-model"] = new(1_000_000, 0, 0, 0, 1_000_000),
            }));

        Assert.Equal(5m, estimate.Cost.Total);
        Assert.Equal(["future-model"], estimate.FallbackModels);
        Assert.Equal("auto", UsagePricing.NormalizeBasis("not-a-model"));
    }

    private static UsageAggregate Aggregate(TokenUsage tokens)
    {
        return new UsageAggregate(tokens, 1, new Dictionary<string, TokenUsage>());
    }
}
