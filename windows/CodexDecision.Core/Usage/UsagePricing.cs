namespace CodexDecision.Core.Usage;

public static class UsagePricing
{
    public const string PricingUpdatedAt = "2026-07-14";
    public const string PricingSourceUrl = "https://developers.openai.com/api/docs/models/compare";
    public const string DefaultModelId = "gpt-5.5";

    public static IReadOnlyList<UsagePricingModel> Models { get; } =
    [
        new("gpt-5.6-sol", "GPT-5.6 Sol", 5m, 0.5m, 30m),
        new("gpt-5.6-terra", "GPT-5.6 Terra", 2.5m, 0.25m, 15m),
        new("gpt-5.6-luna", "GPT-5.6 Luna", 1m, 0.1m, 6m),
        new("gpt-5.5", "GPT-5.5", 5m, 0.5m, 30m),
        new("gpt-5.4", "GPT-5.4", 2.5m, 0.25m, 15m),
        new("gpt-5.4-mini", "GPT-5.4 mini", 0.75m, 0.075m, 4.5m),
        new("gpt-5.3-codex", "GPT-5.3-Codex", 1.75m, 0.175m, 14m),
        new("gpt-5.2", "GPT-5.2", 1.75m, 0.175m, 14m),
        new("gpt-5", "GPT-5", 1.25m, 0.125m, 10m),
    ];

    private static readonly IReadOnlyDictionary<string, UsagePricingModel> Prices =
        Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeBasis(string? value)
    {
        return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ||
               (value is not null && Prices.ContainsKey(value))
            ? value?.ToLowerInvariant() ?? "auto"
            : "auto";
    }

    public static UsageCostEstimate Estimate(UsageAggregate totals, string? basisValue = "auto")
    {
        var basis = NormalizeBasis(basisValue);
        if (basis != "auto")
        {
            var model = Prices[basis];
            return new UsageCostEstimate(
                basis,
                model.Label,
                AtRates(totals.Tokens, model),
                totals.Models.Count,
                []);
        }

        if (totals.Models.Count == 0)
        {
            var fallback = Prices[DefaultModelId];
            return new UsageCostEstimate(
                "auto",
                $"{fallback.Label} fallback",
                AtRates(totals.Tokens, fallback),
                0,
                ["unattributed"]);
        }

        var cost = new UsageCost(0, 0, 0);
        var fallbackModel = Prices[DefaultModelId];
        var fallbackModels = new List<string>();
        foreach (var (modelId, modelTotals) in totals.Models)
        {
            var known = Prices.TryGetValue(modelId, out var model);
            model ??= fallbackModel;
            if (!known)
            {
                fallbackModels.Add(modelId);
            }

            cost = AddCost(cost, AtRates(modelTotals, model));
        }

        return new UsageCostEstimate(
            "auto",
            "Recorded models",
            cost,
            totals.Models.Count,
            fallbackModels);
    }

    private static UsageCost AtRates(TokenUsage totals, UsagePricingModel rates)
    {
        var input = Math.Max(0, totals.InputTokens);
        var cached = Math.Min(input, Math.Max(0, totals.CachedInputTokens));
        var uncached = Math.Max(0, input - cached);
        var output = Math.Max(0, totals.OutputTokens);
        return new UsageCost(
            uncached / 1_000_000m * rates.Input,
            cached / 1_000_000m * rates.CachedInput,
            output / 1_000_000m * rates.Output);
    }

    private static UsageCost AddCost(UsageCost left, UsageCost right)
    {
        return new UsageCost(
            left.UncachedInput + right.UncachedInput,
            left.CachedInput + right.CachedInput,
            left.Output + right.Output);
    }
}
