namespace CodexDecision.Core.Usage;

public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens)
{
    public static TokenUsage Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record UsageAggregate(
    TokenUsage Tokens,
    long Requests,
    IReadOnlyDictionary<string, TokenUsage> Models)
{
    public static UsageAggregate Empty { get; } = new(TokenUsage.Empty, 0, new Dictionary<string, TokenUsage>());

    public long InputTokens => Tokens.InputTokens;

    public long CachedInputTokens => Tokens.CachedInputTokens;

    public long OutputTokens => Tokens.OutputTokens;

    public long ReasoningOutputTokens => Tokens.ReasoningOutputTokens;

    public long TotalTokens => Tokens.TotalTokens;
}

public sealed record DailyUsage(DateOnly Date, UsageAggregate Totals);

public enum UsageLimitState
{
    Normal,
    Warning,
    Critical,
}

public sealed record UsageLimitSnapshot(
    string Id,
    string LimitId,
    string Name,
    string Slot,
    double UsedPercent,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt,
    DateTimeOffset? ObservedAt,
    UsageLimitState State);

public enum UsageScanPhase
{
    Starting,
    Indexing,
    Ready,
    Error,
}

public sealed record UsageScanStatus(
    UsageScanPhase Phase,
    int FilesFound,
    int FilesProcessed,
    long BytesFound,
    long BytesProcessed,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error)
{
    public int Percent => FilesFound > 0
        ? (int)Math.Round((double)FilesProcessed / FilesFound * 100)
        : Phase is UsageScanPhase.Ready ? 100 : 0;
}

public sealed record UsageSource(
    string Label,
    bool LocalOnly,
    int FilesIndexed,
    DateTimeOffset? FirstEventAt,
    UsageScanStatus Scan);

public sealed record UsageAccount(string? Plan);

public sealed record UsageActivity(UsageAggregate AllTime, IReadOnlyList<DailyUsage> Daily);

public sealed record UsageSnapshot(
    DateTimeOffset GeneratedAt,
    UsageSource Source,
    UsageAccount Account,
    IReadOnlyList<UsageLimitSnapshot> Limits,
    UsageActivity Activity,
    string Caveat);

public enum UsageCycleMode
{
    Calendar,
    Subscription,
}

public sealed record UsageCycle(
    string Key,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsCurrent,
    UsageAggregate Totals,
    IReadOnlyList<DailyUsage> Daily);

public sealed record UsageCycleModel(
    UsageCycleMode Mode,
    int? BillingDay,
    string CurrentCycleKey,
    IReadOnlyList<UsageCycle> Cycles);

public sealed record UsagePricingModel(
    string Id,
    string Label,
    decimal Input,
    decimal CachedInput,
    decimal Output);

public sealed record UsageCost(decimal UncachedInput, decimal CachedInput, decimal Output)
{
    public decimal Total => UncachedInput + CachedInput + Output;
}

public sealed record UsageCostEstimate(
    string Basis,
    string Label,
    UsageCost Cost,
    int RecordedModels,
    IReadOnlyList<string> FallbackModels);

public sealed record UsagePreferences(int? BillingDay, string PricingBasis)
{
    public static UsagePreferences Default { get; } = new(null, "auto");
}
